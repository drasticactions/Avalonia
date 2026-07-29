using System;
using System.Collections.Generic;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using SkiaSharp;
using static Avalonia.OpenGL.GlConsts;

namespace Avalonia.Skia;

internal class GlSkiaExternalObjectsFeature : IExternalObjectsRenderInterfaceContextFeature, IGlSkiaFboProvider
{
    private const int GL_TEXTURE_EXTERNAL_OES = 0x8D65;
    private const int GL_TRIANGLE_STRIP_ = 0x0005;
    private const int GL_CURRENT_PROGRAM_ = 0x8B8D;

    private readonly GlSkiaGpu _gpu;
    private readonly IGlContextExternalObjectsFeature? _feature;
    private int _fbo;
    private int _oesProgram;
    private int _oesVbo;
    private int _oesSamplerLocation;

    public GlSkiaExternalObjectsFeature(GlSkiaGpu gpu, IGlContextExternalObjectsFeature? feature)
    {
        _gpu = gpu;
        _feature = feature;
    }

    public IReadOnlyList<string> SupportedImageHandleTypes => _feature?.SupportedImportableExternalImageTypes
                                                              ?? Array.Empty<string>();
    public IReadOnlyList<string> SupportedSemaphoreTypes => _feature?.SupportedImportableExternalSemaphoreTypes
                                                            ?? Array.Empty<string>();
    public IReadOnlyList<PlatformGraphicsDrmFormat>? SupportedDmaBufFormats => _feature?.SupportedDmaBufFormats;

    public IPlatformRenderInterfaceImportedImage ImportImage(IPlatformHandle handle,
        PlatformGraphicsExternalImageProperties properties)
    {
        if (_feature == null)
            throw new NotSupportedException("Importing this platform handle is not supported");
        using (_gpu.EnsureCurrent())
        {
            var image = _feature.ImportImage(handle, properties);
            return new GlSkiaImportedImage(_gpu, this, image);
        }
    }

    public IPlatformRenderInterfaceImportedImage ImportImage(ICompositionImportableSharedGpuContextImage image)
    {
        var img = (GlSkiaSharedTextureForComposition)image;
        if (!img.Context.IsSharedWith(_gpu.GlContext))
            throw new InvalidOperationException("Contexts do not belong to the same share group");
        
        return new GlSkiaImportedImage(_gpu, this, img);
    }

    public IPlatformRenderInterfaceImportedSemaphore ImportSemaphore(IPlatformHandle handle)
    {
        if (_feature == null)
            throw new NotSupportedException("Importing this platform handle is not supported");
        using (_gpu.EnsureCurrent())
        {
            var semaphore = _feature.ImportSemaphore(handle);
            return new GlSkiaImportedSemaphore(_gpu, semaphore);
        }
    }

    public CompositionGpuImportedImageSynchronizationCapabilities GetSynchronizationCapabilities(string imageHandleType)
        => _feature?.GetSynchronizationCapabilities(imageHandleType) ?? default;

    public int Fbo
    {
        get
        {
            if (_fbo == 0)
                _fbo = _gpu.GlContext.GlInterface.GenFramebuffer();

            return _fbo;
        }
    }

    public byte[]? DeviceUuid => _feature?.DeviceUuid;
    public byte[]? DeviceLuid => _feature?.DeviceLuid;

    public int BlitExternalToRgba(int sourceTextureId, int width, int height)
    {
        var gl = _gpu.GlContext.GlInterface;
        using var _ = _gpu.EnsureCurrent();
        EnsureOesProgram(gl);

        gl.GetIntegerv(GL_FRAMEBUFFER_BINDING, out var oldFbo);
        gl.GetIntegerv(GL_CURRENT_PROGRAM_, out var oldProgram);

        var dest = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, dest);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);

        gl.BindFramebuffer(GL_FRAMEBUFFER, Fbo);
        gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, dest, 0);
        gl.Viewport(0, 0, width, height);

        gl.UseProgram(_oesProgram);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_EXTERNAL_OES, sourceTextureId);
        gl.Uniform1i(_oesSamplerLocation, 0);

        gl.BindBuffer(GL_ARRAY_BUFFER, _oesVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 4, GL_FLOAT, 0, 4 * sizeof(float), IntPtr.Zero);
        gl.DrawArrays(GL_TRIANGLE_STRIP_, 0, 4);

        gl.BindTexture(GL_TEXTURE_EXTERNAL_OES, 0);
        gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, 0, 0);
        gl.BindFramebuffer(GL_FRAMEBUFFER, oldFbo);
        gl.UseProgram(oldProgram);
        gl.Flush();
        return dest;
    }

    private void EnsureOesProgram(GlInterface gl)
    {
        if (_oesProgram != 0)
            return;

        const string vertex =
            "attribute vec4 aPos;\n" +
            "varying vec2 vTex;\n" +
            "void main() {\n" +
            "  vTex = aPos.zw;\n" +
            "  gl_Position = vec4(aPos.xy, 0.0, 1.0);\n" +
            "}\n";
        const string fragment =
            "#extension GL_OES_EGL_image_external : require\n" +
            "precision mediump float;\n" +
            "varying vec2 vTex;\n" +
            "uniform samplerExternalOES uTex;\n" +
            "void main() { gl_FragColor = texture2D(uTex, vTex); }\n";

        var vs = gl.CreateShader(GL_VERTEX_SHADER);
        var vsErr = gl.CompileShaderAndGetError(vs, vertex);
        if (vsErr is not null)
            throw new OpenGlException("External-image blit vertex shader: " + vsErr);
        var fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        var fsErr = gl.CompileShaderAndGetError(fs, fragment);
        if (fsErr is not null)
            throw new OpenGlException("External-image blit fragment shader: " + fsErr);

        _oesProgram = gl.CreateProgram();
        gl.AttachShader(_oesProgram, vs);
        gl.AttachShader(_oesProgram, fs);
        gl.BindAttribLocationString(_oesProgram, 0, "aPos");
        var linkErr = gl.LinkProgramAndGetError(_oesProgram);
        if (linkErr is not null)
            throw new OpenGlException("External-image blit program: " + linkErr);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        _oesSamplerLocation = gl.GetUniformLocationString(_oesProgram, "uTex");

        float[] quad =
        {
            -1f, -1f, 0f, 1f,
             1f, -1f, 1f, 1f,
            -1f,  1f, 0f, 0f,
             1f,  1f, 1f, 0f,
        };
        _oesVbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _oesVbo);
        unsafe
        {
            fixed (float* p = quad)
                gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(quad.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
        }
    }
}

internal interface IGlSkiaFboProvider
{
    int Fbo { get; }

    /// <summary>
    /// Samples an external OES texture (YUV / NV12) into a fresh RGBA GL_TEXTURE_2D that Skia
    /// can consume, doing the colour conversion via samplerExternalOES. Returns the new id.
    /// </summary>
    int BlitExternalToRgba(int sourceTextureId, int width, int height);
}

internal class GlSkiaImportedSemaphore : IPlatformRenderInterfaceImportedSemaphore
{
    private readonly GlSkiaGpu _gpu;
    public IGlExternalSemaphore Semaphore { get; }

    public GlSkiaImportedSemaphore(GlSkiaGpu gpu, IGlExternalSemaphore semaphore)
    {
        _gpu = gpu;
        Semaphore = semaphore;
    }

    public void Dispose() => Semaphore.Dispose();
}

internal class GlSkiaImportedImage : IPlatformRenderInterfaceImportedImage
{
    private readonly GlSkiaSharedTextureForComposition? _sharedTexture;
    private readonly GlSkiaGpu _gpu;
    private readonly IGlSkiaFboProvider _fboProvider;
    private readonly IGlExternalImageTexture? _image;

    public GlSkiaImportedImage(GlSkiaGpu gpu, IGlSkiaFboProvider fboProvider, IGlExternalImageTexture image)
    {
        _gpu = gpu;
        _fboProvider = fboProvider;
        _image = image;
    }

    public GlSkiaImportedImage(GlSkiaGpu gpu, IGlSkiaFboProvider fboProvider, GlSkiaSharedTextureForComposition sharedTexture)
    {
        _gpu = gpu;
        _fboProvider = fboProvider;
        _sharedTexture = sharedTexture;
    }

    public void Dispose()
    {
        _image?.Dispose();
        _sharedTexture?.Dispose(_gpu.GlContext);
    }

    SKColorType ConvertColorType(PlatformGraphicsExternalImageFormat format) =>
        format switch
        {
            PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm => SKColorType.Bgra8888,
            PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm => SKColorType.Rgba8888,
            _ => SKColorType.Rgba8888
        };

    SKImage? TryCreateImage(int target, int textureId, int format, int width, int height, bool topLeft)
    {
        var origin = topLeft ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft;

        using var texture = new GRBackendTexture(width, height, false,
            new GRGlTextureInfo((uint)target, (uint)textureId, (uint)format));

        var image = SKImage.FromAdoptedTexture(_gpu.GrContext, texture, origin, SKColorType.Rgba8888);
        if (image is not null)
            return image;

        using var unformatted = new GRBackendTexture(width, height, false,
            new GRGlTextureInfo((uint)target, (uint)textureId));

        return SKImage.FromAdoptedTexture(_gpu.GrContext, unformatted, origin, SKColorType.Rgba8888);
    }

    IBitmapImpl TakeSnapshot()
    {
        var width = _image?.Properties.Width ?? _sharedTexture!.Size.Width;
        var height = _image?.Properties.Height ?? _sharedTexture!.Size.Height;
        var internalFormat = _image?.InternalFormat ?? _sharedTexture!.InternalFormat;
        var textureId = _image?.TextureId ?? _sharedTexture!.TextureId;
        var topLeft = _image?.Properties.TopLeftOrigin ?? false;
        var textureType = _image?.TextureType ?? GL_TEXTURE_2D;

        var context = _gpu.GlContext;
        var snapshotTextureId = CopyToNewTexture(textureType, textureId, internalFormat, width, height);
        var snapshotImage = TryCreateImage(textureType, snapshotTextureId, internalFormat, width, height, topLeft);

        if (snapshotImage is null)
        {
            context.GlInterface.DeleteTexture(snapshotTextureId);
            throw new OpenGlException("Unable to consume provided texture");
        }

        var rv = new ImmutableBitmap(snapshotImage, () =>
        {
            IDisposable? restoreContext = null;
            try
            {
                restoreContext = context.EnsureCurrent();
            }
            catch
            {
                // Ignore, context is likely dead
            }

            using (restoreContext)
            {
                snapshotImage.Dispose();
            }
        });

        _gpu.GrContext.Flush();
        context.GlInterface.Flush();
        return rv;
    }

    public IBitmapImpl SnapshotWithKeyedMutex(uint acquireIndex, uint releaseIndex)
    {
        if (_image is null)
        {
            throw new NotSupportedException("Only supported with an external image");
        }

        using (_gpu.EnsureCurrent())
        {
            _image.AcquireKeyedMutex(acquireIndex);
            try
            {
                return TakeSnapshot();
            }
            finally
            {
                _image.ReleaseKeyedMutex(releaseIndex);
            }
        }
    }

    public IBitmapImpl SnapshotWithSemaphores(IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
        IPlatformRenderInterfaceImportedSemaphore signalSemaphore)
    {
        if (_image is null)
        {
            throw new NotSupportedException("Only supported with an external image");
        }

        var wait = (GlSkiaImportedSemaphore)waitForSemaphore;
        var signal = (GlSkiaImportedSemaphore)signalSemaphore;
        using (_gpu.EnsureCurrent())
        {
            wait.Semaphore.WaitSemaphore(_image);
            try
            {
                return TakeSnapshot();
            }
            finally
            {
                signal.Semaphore.SignalSemaphore(_image);
            }
        }
    }

    public IBitmapImpl SnapshotWithTimelineSemaphores(IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
        ulong waitForValue, IPlatformRenderInterfaceImportedSemaphore signalSemaphore, ulong signalValue)
    {
        if (_image is null)
        {
            throw new NotSupportedException("Only supported with an external image");
        }

        var wait = (GlSkiaImportedSemaphore)waitForSemaphore;
        var signal = (GlSkiaImportedSemaphore)signalSemaphore;
        using (_gpu.EnsureCurrent())
        {
            wait.Semaphore.WaitTimelineSemaphore(_image, waitForValue);
            try
            {
                return TakeSnapshot();
            }
            finally
            {
                signal.Semaphore.SignalTimelineSemaphore(_image, signalValue);
            }
        }
    }

    public IBitmapImpl SnapshotWithAutomaticSync()
    {
        using (_gpu.EnsureCurrent())
            return TakeSnapshot();
    }

    private int CopyToNewTexture(int textureType, int sourceTextureId, int internalFormat, int width, int height)
    {
        var gl = _gpu.GlContext.GlInterface;

        using var _ = _gpu.EnsureCurrent();

        // External OES textures (YUV / NV12) can't be a framebuffer read source; sample them
        // through samplerExternalOES into a normal RGBA GL_TEXTURE_2D that Skia can consume.
        if (textureType == GL_TEXTURE_EXTERNAL_OES)
            return _fboProvider.BlitExternalToRgba(sourceTextureId, width, height);

        // Snapshot current values
        gl.GetIntegerv(GL_FRAMEBUFFER_BINDING, out var oldFbo);
        gl.GetIntegerv(GL_SCISSOR_TEST, out var oldScissorTest);

        // Bind source texture
        gl.BindFramebuffer(GL_FRAMEBUFFER, _fboProvider.Fbo);
        gl.Disable(GL_SCISSOR_TEST);
        gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, textureType, sourceTextureId, 0);

        // Create destination texture
        var destTextureId = gl.GenTexture();
        gl.BindTexture(textureType, destTextureId);
        gl.TexImage2D(textureType, 0, internalFormat, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);

        // Copy
        gl.CopyTexSubImage2D(textureType, 0, 0, 0, 0, 0, width, height);

        // Flush
        gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, textureType, 0, 0);
        gl.Flush();

        // Restore old values
        gl.BindFramebuffer(GL_FRAMEBUFFER, oldFbo);
        if (oldScissorTest != 0)
            gl.Enable(GL_SCISSOR_TEST);

        return destTextureId;
    }
}
