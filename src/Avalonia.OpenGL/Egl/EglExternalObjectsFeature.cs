using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using static Avalonia.OpenGL.Egl.EglConsts;
using static Avalonia.OpenGL.GlConsts;

namespace Avalonia.OpenGL.Egl;

internal class EglExternalObjectsFeature : IGlContextExternalObjectsFeature
{
    private readonly EglContext _context;
    private readonly bool _hasModifiers;
    private readonly List<string> _imageTypes = new();

    public static EglExternalObjectsFeature? TryCreate(EglContext context)
    {
        try
        {
            var egl = context.Display.EglInterface;
            if (!egl.IsCreateImageKHRAvailable || !egl.IsDestroyImageKHRAvailable)
                return null;

            var eglExtensions = egl.QueryString(context.Display.Handle, EGL_EXTENSIONS);
            if (eglExtensions == null || !eglExtensions.Contains("EGL_EXT_image_dma_buf_import"))
                return null;

            if (!context.GlInterface.GetExtensions().Contains("GL_OES_EGL_image"))
                return null;

            var hasModifiers = eglExtensions.Contains("EGL_EXT_image_dma_buf_import_modifiers");
            return new EglExternalObjectsFeature(context, hasModifiers);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, "OpenGL")?.Log(nameof(EglExternalObjectsFeature),
                "Unable to initialize EGL dma-buf import feature: " + e);
            return null;
        }
    }

    private readonly bool _hasNativeFenceSync;
    private readonly List<string> _semaphoreTypes = new();

    private EglExternalObjectsFeature(EglContext context, bool hasModifiers)
    {
        _context = context;
        _hasModifiers = hasModifiers;
        _imageTypes.Add(KnownPlatformGraphicsExternalImageHandleTypes.DmaBufFileDescriptor);

        var egl = context.Display.EglInterface;
        var extensions = egl.QueryString(context.Display.Handle, EGL_EXTENSIONS) ?? "";
        _hasNativeFenceSync = extensions.Contains("EGL_KHR_fence_sync")
            && extensions.Contains("EGL_ANDROID_native_fence_sync")
            && egl.IsCreateSyncKHRAvailable && egl.IsDupNativeFenceFDANDROIDAvailable
            && egl.IsWaitSyncKHRAvailable && egl.IsDestroySyncKHRAvailable;
        if (_hasNativeFenceSync)
            _semaphoreTypes.Add(KnownPlatformGraphicsExternalSemaphoreHandleTypes.SyncFileDescriptor);
    }

    public IReadOnlyList<string> SupportedImportableExternalImageTypes => _imageTypes;
    public IReadOnlyList<string> SupportedExportableExternalImageTypes { get; } = Array.Empty<string>();
    public IReadOnlyList<string> SupportedImportableExternalSemaphoreTypes => _semaphoreTypes;
    public IReadOnlyList<string> SupportedExportableExternalSemaphoreTypes => _semaphoreTypes;

    public IReadOnlyList<PlatformGraphicsExternalImageFormat> GetSupportedFormatsForExternalMemoryType(string type) =>
        new[]
        {
            PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
        };

    private bool _dmaBufFormatsQueried;
    private IReadOnlyList<PlatformGraphicsDrmFormat>? _dmaBufFormats;

    public IReadOnlyList<PlatformGraphicsDrmFormat>? SupportedDmaBufFormats
    {
        get
        {
            if (!_dmaBufFormatsQueried)
            {
                using (_context.Display.Lock())
                {
                    if (!_dmaBufFormatsQueried)
                    {
                        _dmaBufFormats = TryQueryDmaBufFormats();
                        _dmaBufFormatsQueried = true;
                    }
                }
            }
            return _dmaBufFormats;
        }
    }

    private unsafe IReadOnlyList<PlatformGraphicsDrmFormat>? TryQueryDmaBufFormats()
    {
        var egl = _context.Display.EglInterface;
        var display = _context.Display.Handle;
        if (!_hasModifiers || !egl.IsQueryDmaBufFormatsEXTAvailable || !egl.IsQueryDmaBufModifiersEXTAvailable)
            return null;

        try
        {
            if (!egl.QueryDmaBufFormatsEXT(display, 0, null, out var formatCount) || formatCount <= 0)
                return null;

            var formats = new int[formatCount];
            fixed (int* formatsPtr = formats)
            {
                if (!egl.QueryDmaBufFormatsEXT(display, formatCount, formatsPtr, out formatCount))
                    return null;
            }

            var result = new List<PlatformGraphicsDrmFormat>();
            for (var f = 0; f < formatCount; f++)
            {
                var format = formats[f];
                if (!egl.QueryDmaBufModifiersEXT(display, format, 0, null, null, out var modifierCount))
                    continue;
                if (modifierCount <= 0)
                {
                    // No explicit modifiers reported: the format imports with the implicit layout.
                    result.Add(new PlatformGraphicsDrmFormat((uint)format,
                        PlatformGraphicsExternalImageProperties.DrmModifierInvalid));
                    continue;
                }

                var modifiers = new ulong[modifierCount];
                // EGLBoolean is 4 bytes; back the external-only buffer with ints.
                var externalOnly = new int[modifierCount];
                fixed (ulong* modifiersPtr = modifiers)
                fixed (int* externalOnlyPtr = externalOnly)
                {
                    if (!egl.QueryDmaBufModifiersEXT(display, format, modifierCount, modifiersPtr,
                            (bool*)externalOnlyPtr, out modifierCount))
                        continue;
                }

                for (var m = 0; m < modifierCount; m++)
                {
                    if (externalOnly[m] == 0 || IsYuvDrmFormat(format))
                        result.Add(new PlatformGraphicsDrmFormat((uint)format, modifiers[m]));
                }
            }

            return result;
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, "OpenGL")?.Log(this,
                "Unable to enumerate EGL dma-buf import formats: " + e);
            return null;
        }
    }

    public IGlExportableExternalImageTexture CreateImage(string type, PixelSize size,
        PlatformGraphicsExternalImageFormat format) => throw new NotSupportedException();

    private static bool IsYuvDrmFormat(int drmFormat)
    {
        // NV12/NV21/NV16/NV24, YUV420 (YU12/YV12), P010/P016, and other planar YUV fourccs
        // have the second byte in {'V'} or start with 'N'/'Y'/'P' planar families.
        var b0 = (byte)drmFormat;
        var b1 = (byte)(drmFormat >> 8);
        return (b0 == (byte)'N' && b1 == (byte)'V')   // NVxx
            || (b0 == (byte)'Y' && (b1 == (byte)'U' || b1 == (byte)'V')) // YUxx/YVxx
            || (b0 == (byte)'P' && (b1 == (byte)'0' || b1 == (byte)'2')); // P010/P016/P210...
    }

    public IGlExportableExternalSemaphore CreateSemaphore(string type)
    {
        if (type != KnownPlatformGraphicsExternalSemaphoreHandleTypes.SyncFileDescriptor || !_hasNativeFenceSync)
            throw new NotSupportedException(type + " semaphores are not supported");
        return new EglNativeFenceSemaphore(_context, importedFd: -1);
    }

    public IGlExternalImageTexture ImportImage(IPlatformHandle handle,
        PlatformGraphicsExternalImageProperties properties)
    {
        if (handle.HandleDescriptor != KnownPlatformGraphicsExternalImageHandleTypes.DmaBufFileDescriptor)
            throw new ArgumentException(handle.HandleDescriptor + " is not supported", nameof(handle));

        var planeCount = properties.PlaneCount > 0 ? properties.PlaneCount : 1;

        var attribs = new List<int>
        {
            EGL_WIDTH, properties.Width,
            EGL_HEIGHT, properties.Height,
            EGL_LINUX_DRM_FOURCC_EXT, (int)properties.DrmFormat
        };

        for (var p = 0; p < planeCount; p++)
        {
            var fd = properties.PlaneFds is { } fds ? fds[p] : handle.Handle.ToInt32();
            var offset = properties.PlaneOffsets is { } offsets ? (int)offsets[p] : (int)properties.MemoryOffset;
            var pitch = properties.PlaneStrides is { } strides ? (int)strides[p] : 0;

            attribs.Add(PlaneFdAttrib(p));
            attribs.Add(fd);
            attribs.Add(PlaneOffsetAttrib(p));
            attribs.Add(offset);
            attribs.Add(PlanePitchAttrib(p));
            attribs.Add(pitch);

            if (_hasModifiers && properties.DrmModifier != PlatformGraphicsExternalImageProperties.DrmModifierInvalid)
            {
                attribs.Add(PlaneModifierLoAttrib(p));
                attribs.Add((int)(properties.DrmModifier & 0xFFFFFFFF));
                attribs.Add(PlaneModifierHiAttrib(p));
                attribs.Add((int)(properties.DrmModifier >> 32));
            }
        }

        attribs.Add(EGL_NONE);

        IntPtr imageHandle;
        using (_context.Display.Lock())
            imageHandle = _context.Display.EglInterface.CreateImageKHR(_context.Display.Handle, IntPtr.Zero,
                EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attribs.ToArray());

        if (imageHandle == IntPtr.Zero)
            throw new OpenGlException("eglCreateImageKHR failed to import the dma-buf");

        var eglImage = new EglImage(_context.Display, imageHandle);

        // YUV / multi-planar images must be bound to the external OES target; the driver's
        // samplerExternalOES does the colour conversion. RGB stays on GL_TEXTURE_2D.
        var textureTarget = properties.Format == PlatformGraphicsExternalImageFormat.Yuv
            ? GL_TEXTURE_EXTERNAL_OES
            : GL_TEXTURE_2D;

        var gl = _context.GlInterface;
        gl.GetIntegerv(GL_TEXTURE_BINDING_2D, out var oldTexture);
        var texture = gl.GenTexture();
        try
        {
            gl.BindTexture(textureTarget, texture);
            gl.EGLImageTargetTexture2DOES(textureTarget, eglImage.Handle);
            var err = gl.GetError();
            if (err != 0)
                throw OpenGlException.GetFormattedException("glEGLImageTargetTexture2DOES", err);

            // The imported texture has no mip levels; ensure it is sampling-complete.
            gl.TexParameteri(textureTarget, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
            gl.TexParameteri(textureTarget, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        }
        catch
        {
            gl.BindTexture(textureTarget, oldTexture);
            gl.DeleteTexture(texture);
            eglImage.Dispose();
            throw;
        }

        gl.BindTexture(textureTarget, oldTexture);
        return new DmaBufImageTexture(_context, eglImage, texture, properties, textureTarget);
    }

    public IGlExternalSemaphore ImportSemaphore(IPlatformHandle handle)
    {
        if (handle.HandleDescriptor != KnownPlatformGraphicsExternalSemaphoreHandleTypes.SyncFileDescriptor
            || !_hasNativeFenceSync)
            throw new NotSupportedException(handle.HandleDescriptor + " semaphores are not supported");
        return new EglNativeFenceSemaphore(_context, importedFd: handle.Handle.ToInt32());
    }

    public CompositionGpuImportedImageSynchronizationCapabilities GetSynchronizationCapabilities(string imageHandleType)
    {
        if (imageHandleType == KnownPlatformGraphicsExternalImageHandleTypes.DmaBufFileDescriptor)
            return CompositionGpuImportedImageSynchronizationCapabilities.Automatic;
        return default;
    }

    public byte[]? DeviceLuid => null;
    public byte[]? DeviceUuid => null;

    private static int PlaneFdAttrib(int plane) => plane switch
    {
        0 => EGL_DMA_BUF_PLANE0_FD_EXT,
        1 => EGL_DMA_BUF_PLANE1_FD_EXT,
        2 => EGL_DMA_BUF_PLANE2_FD_EXT,
        3 => EGL_DMA_BUF_PLANE3_FD_EXT,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    private static int PlaneOffsetAttrib(int plane) => plane switch
    {
        0 => EGL_DMA_BUF_PLANE0_OFFSET_EXT,
        1 => EGL_DMA_BUF_PLANE1_OFFSET_EXT,
        2 => EGL_DMA_BUF_PLANE2_OFFSET_EXT,
        3 => EGL_DMA_BUF_PLANE3_OFFSET_EXT,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    private static int PlanePitchAttrib(int plane) => plane switch
    {
        0 => EGL_DMA_BUF_PLANE0_PITCH_EXT,
        1 => EGL_DMA_BUF_PLANE1_PITCH_EXT,
        2 => EGL_DMA_BUF_PLANE2_PITCH_EXT,
        3 => EGL_DMA_BUF_PLANE3_PITCH_EXT,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    private static int PlaneModifierLoAttrib(int plane) => plane switch
    {
        0 => EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT,
        1 => EGL_DMA_BUF_PLANE1_MODIFIER_LO_EXT,
        2 => EGL_DMA_BUF_PLANE2_MODIFIER_LO_EXT,
        3 => EGL_DMA_BUF_PLANE3_MODIFIER_LO_EXT,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    private static int PlaneModifierHiAttrib(int plane) => plane switch
    {
        0 => EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT,
        1 => EGL_DMA_BUF_PLANE1_MODIFIER_HI_EXT,
        2 => EGL_DMA_BUF_PLANE2_MODIFIER_HI_EXT,
        3 => EGL_DMA_BUF_PLANE3_MODIFIER_HI_EXT,
        _ => throw new ArgumentOutOfRangeException(nameof(plane))
    };

    /// <summary>
    /// A semaphore backed by an EGL native fence (EGL_ANDROID_native_fence_sync). Signalling
    /// inserts a fence into the GL command stream and exports it as a sync-file fd; waiting
    /// imports an fd as a fence and issues a GPU-side wait so subsequent sampling of the
    /// associated texture happens only after the producer's work completes.
    /// </summary>
    private sealed class EglNativeFenceSemaphore : IGlExportableExternalSemaphore
    {
        private readonly EglContext _context;
        private int _importedFd;

        public EglNativeFenceSemaphore(EglContext context, int importedFd)
        {
            _context = context;
            _importedFd = importedFd;
        }

        public void SignalSemaphore(IGlExternalImageTexture texture)
        {
            // Create a fence after the producer's GL commands; flush so it enters the stream.
            using var _ = _context.EnsureCurrent();
            var egl = _context.Display.EglInterface;
            var sync = egl.CreateSyncKHR(_context.Display.Handle, EGL_SYNC_NATIVE_FENCE_ANDROID, null);
            if (sync == IntPtr.Zero)
                throw new OpenGlException("eglCreateSyncKHR (native fence) failed");
            try
            {
                _context.GlInterface.Flush();
                var fd = egl.DupNativeFenceFDANDROID(_context.Display.Handle, sync);
                if (fd == EGL_NO_NATIVE_FENCE_FD_ANDROID)
                    throw new OpenGlException("eglDupNativeFenceFDANDROID failed");
                if (_importedFd >= 0)
                    NativeUnixMethods.close(_importedFd);
                _importedFd = fd;
            }
            finally
            {
                egl.DestroySyncKHR(_context.Display.Handle, sync);
            }
        }

        public void WaitSemaphore(IGlExternalImageTexture texture)
        {
            if (_importedFd < 0)
                return;
            using var _ = _context.EnsureCurrent();
            var egl = _context.Display.EglInterface;
            // Creating the sync from the fd transfers ownership of the fd to EGL.
            var attribs = new[] { EGL_SYNC_NATIVE_FENCE_FD_ANDROID, _importedFd, EGL_NONE };
            var sync = egl.CreateSyncKHR(_context.Display.Handle, EGL_SYNC_NATIVE_FENCE_ANDROID, attribs);
            _importedFd = -1;
            if (sync == IntPtr.Zero)
                throw new OpenGlException("eglCreateSyncKHR from imported fence fd failed");
            try
            {
                egl.WaitSyncKHR(_context.Display.Handle, sync, 0);
            }
            finally
            {
                egl.DestroySyncKHR(_context.Display.Handle, sync);
            }
        }

        public void WaitTimelineSemaphore(IGlExternalImageTexture texture, ulong value) => WaitSemaphore(texture);

        public void SignalTimelineSemaphore(IGlExternalImageTexture texture, ulong value) => SignalSemaphore(texture);

        public IPlatformHandle GetHandle()
        {
            if (_importedFd < 0)
                throw new InvalidOperationException("The semaphore has not been signalled.");
            var fd = _importedFd;
            _importedFd = -1;
            return new PlatformHandle(new IntPtr(fd), KnownPlatformGraphicsExternalSemaphoreHandleTypes.SyncFileDescriptor);
        }

        public void Dispose()
        {
            if (_importedFd >= 0)
            {
                NativeUnixMethods.close(_importedFd);
                _importedFd = -1;
            }
        }
    }

    private static class NativeUnixMethods
    {
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);
    }

    private sealed class DmaBufImageTexture : IGlExternalImageTexture
    {
        private readonly EglContext _context;
        private readonly int _textureType;
        private EglImage? _image;
        private int _texture;

        public DmaBufImageTexture(EglContext context, EglImage image, int texture,
            PlatformGraphicsExternalImageProperties properties, int textureType = GL_TEXTURE_2D)
        {
            _context = context;
            _image = image;
            _texture = texture;
            _textureType = textureType;
            Properties = properties;
        }

        public void Dispose()
        {
            if (_context.IsLost)
                return;
            using (_context.EnsureCurrent())
            {
                if (_texture != 0)
                {
                    _context.GlInterface.DeleteTexture(_texture);
                    _texture = 0;
                }
                _image?.Dispose();
                _image = null;
            }
        }

        public void AcquireKeyedMutex(uint key) => throw new NotSupportedException();
        public void ReleaseKeyedMutex(uint key) => throw new NotSupportedException();

        public int TextureId => _texture;
        public int InternalFormat => GL_RGBA8;
        public int TextureType => _textureType;
        public PlatformGraphicsExternalImageProperties Properties { get; }
    }
}