using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Input.Raw;
using static Avalonia.X11.XLib;

namespace Avalonia.X11
{
    internal unsafe class XI2Manager
    {
        private static readonly XiEventType[] DefaultEventTypes = new XiEventType[]
        {
            XiEventType.XI_Motion,
            XiEventType.XI_ButtonPress,
            XiEventType.XI_ButtonRelease,
            XiEventType.XI_Leave,
            XiEventType.XI_Enter,
            
        };

        private static readonly XiEventType[] MultiTouchEventTypes = new XiEventType[]
        {
            XiEventType.XI_TouchBegin,
            XiEventType.XI_TouchUpdate,
            XiEventType.XI_TouchEnd
        };

        private X11Info _x11;
        private bool _multitouch;
        private Dictionary<IntPtr, IXI2Client> _clients = new Dictionary<IntPtr, IXI2Client>();

        private class DeviceInfo
        {
            public int Id { get; }
            public XIValuatorClassInfo[] Valuators { get; private set; } = [];
            public XIScrollClassInfo[] Scrollers { get; private set; } = [];
            public DeviceInfo(XIDeviceInfo info)
            {
                Id = info.Deviceid;
                UpdateCore(info.Classes, info.NumClasses);
            }

            public virtual void Update(XIAnyClassInfo** classes, int num)
            {
                UpdateCore(classes, num);
            }

            private void UpdateCore(XIAnyClassInfo** classes, int num)
            {
                var valuators = new List<XIValuatorClassInfo>();
                var scrollers = new List<XIScrollClassInfo>();
                for (var c = 0; c < num; c++)
                {
                    if (classes[c]->Type == XiDeviceClass.XIValuatorClass)
                        valuators.Add(*((XIValuatorClassInfo**)classes)[c]);
                    if (classes[c]->Type == XiDeviceClass.XIScrollClass)
                        scrollers.Add(*((XIScrollClassInfo**)classes)[c]);
                }

                Valuators = valuators.ToArray();
                Scrollers = scrollers.ToArray();
            }

            public void UpdateValuators(Dictionary<int, double> valuators)
            {
                foreach (var v in valuators)
                {
                    if (Valuators.Length > v.Key) 
                        Valuators[v.Key].Value = v.Value;
                }
            }
        }

        private class PointerDeviceInfo : DeviceInfo
        {
            public PointerDeviceInfo(XIDeviceInfo info, X11Info x11Info) : base(info)
            {
                _x11 = x11Info;

                UpdateKnownValuator();
            }

            private readonly X11Info _x11;

            private void UpdateKnownValuator()
            {
                // ABS_MT_TOUCH_MAJOR ABS_MT_TOUCH_MINOR
                // https://www.kernel.org/doc/html/latest/input/multi-touch-protocol.html
                var touchMajorAtom = XInternAtom(_x11.Display, "Abs MT Touch Major", false);
                var touchMinorAtom = XInternAtom(_x11.Display, "Abs MT Touch Minor", false);

                var pressureAtom = XInternAtom(_x11.Display, "Abs MT Pressure", false);

                PressureXIValuatorClassInfo = null;
                TouchMajorXIValuatorClassInfo = null;
                TouchMinorXIValuatorClassInfo = null;

                foreach (var xiValuatorClassInfo in Valuators)
                {
                    if (xiValuatorClassInfo.Label == pressureAtom)
                    {
                        PressureXIValuatorClassInfo = xiValuatorClassInfo;
                    }
                    else if (xiValuatorClassInfo.Label == touchMajorAtom)
                    {
                        TouchMajorXIValuatorClassInfo = xiValuatorClassInfo;
                    }
                    else if (xiValuatorClassInfo.Label == touchMinorAtom)
                    {
                        TouchMinorXIValuatorClassInfo = xiValuatorClassInfo;
                    }
                }
            }

            public override void Update(XIAnyClassInfo** classes, int num)
            {
                base.Update(classes, num);
                UpdateKnownValuator();
            }

            public bool HasScroll(ParsedDeviceEvent ev)
            {
                foreach (var val in ev.Valuators)
                    if (Scrollers.Any(s => s.Number == val.Key))
                        return true;

                return false;
            }
            
            public bool HasMotion(ParsedDeviceEvent ev)
            {
                foreach (var val in ev.Valuators)
                    if (Scrollers.All(s => s.Number != val.Key))
                        return true;

                return false;
            }

            public XIValuatorClassInfo? PressureXIValuatorClassInfo { get; private set; }
            public XIValuatorClassInfo? TouchMajorXIValuatorClassInfo { get; private set; }
            public XIValuatorClassInfo? TouchMinorXIValuatorClassInfo { get; private set; }
        }
        
        private readonly PointerDeviceInfo _pointerDevice;
        private readonly AvaloniaX11Platform _platform;

        private XI2Manager(AvaloniaX11Platform platform, PointerDeviceInfo pointerDevice)
        {
            _platform = platform;
            _x11 = platform.Info;
            _multitouch = platform.Options.EnableMultiTouch ?? true;
            _pointerDevice = pointerDevice;
        }

        public static XI2Manager? TryCreate(AvaloniaX11Platform platform)
        {
            var x11 = platform.Info;

            var devices = (XIDeviceInfo*) XIQueryDevice(x11.Display,
                (int)XiPredefinedDeviceId.XIAllMasterDevices, out int num);

            PointerDeviceInfo? pointerDevice = null;

            for (var c = 0; c < num; c++)
            {
                if (devices[c].Use == XiDeviceType.XIMasterPointer)
                {
                    pointerDevice = new PointerDeviceInfo(devices[c], x11);
                    break;
                }
            }

            if (pointerDevice is null)
                return null;

            var status = XiSelectEvents(
                x11.Display,
                x11.RootWindow,
                new Dictionary<int, List<XiEventType>> { [pointerDevice.Id] = [XiEventType.XI_DeviceChanged] });

            if (status != Status.Success)
                return null;

            return new XI2Manager(platform, pointerDevice);
        }

        public XEventMask AddWindow(IntPtr xid, IXI2Client window)
        {
            _clients[xid] = window;

            var eventsLength = DefaultEventTypes.Length;

            if (_multitouch)
                eventsLength += MultiTouchEventTypes.Length;

            var events = new List<XiEventType>(eventsLength);

            events.AddRange(DefaultEventTypes);

            if (_multitouch)
                events.AddRange(MultiTouchEventTypes);

            XiSelectEvents(_x11.Display, xid,
                new Dictionary<int, List<XiEventType>> {[_pointerDevice.Id] = events});
                
            // We are taking over mouse input handling from here
            return XEventMask.PointerMotionMask
                   | XEventMask.ButtonMotionMask
                   | XEventMask.Button1MotionMask
                   | XEventMask.Button2MotionMask
                   | XEventMask.Button3MotionMask
                   | XEventMask.Button4MotionMask
                   | XEventMask.Button5MotionMask
                   | XEventMask.ButtonPressMask
                   | XEventMask.ButtonReleaseMask
                   | XEventMask.LeaveWindowMask
                   | XEventMask.EnterWindowMask;
        }

        public void OnWindowDestroyed(IntPtr xid) => _clients.Remove(xid);
        
        public void OnEvent(XIEvent* xev)
        {
            if (xev->evtype == XiEventType.XI_DeviceChanged)
            {
                var changed = (XIDeviceChangedEvent*)xev;
                _pointerDevice.Update(changed->Classes, changed->NumClasses);
            }


            if ((xev->evtype >= XiEventType.XI_ButtonPress && xev->evtype <= XiEventType.XI_Motion)
                || (xev->evtype >= XiEventType.XI_TouchBegin && xev->evtype <= XiEventType.XI_TouchEnd))
            {
                var dev = (XIDeviceEvent*)xev;
                if (_clients.TryGetValue(dev->EventWindow, out var client))
                    OnDeviceEvent(client, new ParsedDeviceEvent(dev));
            }

            if (xev->evtype == XiEventType.XI_Leave || xev->evtype == XiEventType.XI_Enter)
            {
                var rev = (XIEnterLeaveEvent*)xev;
                if (_clients.TryGetValue(rev->EventWindow, out var client))
                    OnEnterLeaveEvent(client, ref *rev);
            }
        }

        private void OnEnterLeaveEvent(IXI2Client client, ref XIEnterLeaveEvent ev)
        {
            if (ev.evtype == XiEventType.XI_Leave)
            {
                var buttons = ParsedDeviceEvent.ParseButtonState(ev.buttons.MaskLen, ev.buttons.Mask);
                var detail = ev.detail;
                if ((detail == XiEnterLeaveDetail.XINotifyNonlinearVirtual ||
                     detail == XiEnterLeaveDetail.XINotifyNonlinear ||
                     detail == XiEnterLeaveDetail.XINotifyVirtual)
                    && buttons == default)
                {
                    foreach (var scroller in _pointerDevice.Scrollers)
                    {
                       _pointerDevice.Valuators[scroller.Number].Value = 0;
                    }
                    
                    client.ScheduleXI2Input(new RawPointerEventArgs(client.MouseDevice, (ulong)ev.time.ToInt64(),
                        client.InputRoot,
                        RawPointerEventType.LeaveWindow, new Point(ev.event_x, ev.event_y), buttons));
                }
            }
        }

        private void OnDeviceEvent(IXI2Client client, ParsedDeviceEvent ev)
        {
            if (ev.Type == XiEventType.XI_TouchBegin 
                || ev.Type == XiEventType.XI_TouchUpdate 
                || ev.Type == XiEventType.XI_TouchEnd)
            {
                var type = ev.Type == XiEventType.XI_TouchBegin ?
                    RawPointerEventType.TouchBegin :
                    (ev.Type == XiEventType.XI_TouchUpdate ?
                        RawPointerEventType.TouchUpdate :
                        RawPointerEventType.TouchEnd);

                var rawPointerPoint = new RawPointerPoint()
                {
                    Position = ev.Position
                };

                if (_pointerDevice.PressureXIValuatorClassInfo is {} valuatorClassInfo)
                {
                    if (ev.Valuators.TryGetValue(valuatorClassInfo.Number, out var pressureValue))
                    {
                        // In our API we use range from 0.0 to 1.0.
                        var pressure = (pressureValue - valuatorClassInfo.Min) / (valuatorClassInfo.Max - valuatorClassInfo.Min);
                        rawPointerPoint.Pressure = (float)pressure;
                    }
                }

                if(_pointerDevice.TouchMajorXIValuatorClassInfo is {} touchMajorXIValuatorClassInfo)
                {
                    double? touchMajor = null;
                    double? touchMinor = null;
                    PixelRect screenBounds = default;
                    if (ev.Valuators.TryGetValue(touchMajorXIValuatorClassInfo.Number, out var touchMajorValue))
                    {
                        var pixelPoint = new PixelPoint((int)ev.RootPosition.X, (int)ev.RootPosition.Y);
                        var screen = _platform.Screens.ScreenFromPoint(pixelPoint);
                        if (screen?.Bounds is { } screenBoundsFromPoint)
                        {
                            screenBounds = screenBoundsFromPoint;

                            // As https://www.kernel.org/doc/html/latest/input/multi-touch-protocol.html says, using `screenBounds.Width` is not accurate enough.
                            touchMajor = (touchMajorValue - touchMajorXIValuatorClassInfo.Min) /
                                (touchMajorXIValuatorClassInfo.Max - touchMajorXIValuatorClassInfo.Min) * screenBounds.Width;
                        }
                    }

                    if (touchMajor != null)
                    {
                        if(_pointerDevice.TouchMinorXIValuatorClassInfo is {} touchMinorXIValuatorClassInfo)
                        {
                            if (ev.Valuators.TryGetValue(touchMinorXIValuatorClassInfo.Number, out var touchMinorValue))
                            {
                                touchMinor = (touchMinorValue - touchMinorXIValuatorClassInfo.Min) /
                                             (touchMinorXIValuatorClassInfo.Max - touchMinorXIValuatorClassInfo.Min) * screenBounds.Height;
                            }
                        }

                        if (touchMinor == null)
                        {
                            touchMinor = touchMajor;
                        }

                        var center = ev.Position;
                        var leftX = center.X - touchMajor.Value / 2;
                        var topY = center.Y - touchMinor.Value / 2;

                        rawPointerPoint.ContactRect = new Rect
                        (
                            leftX,
                            topY,
                            touchMajor.Value,
                            touchMinor.Value
                        );
                    }
                }

                client.ScheduleXI2Input(new RawTouchEventArgs(client.TouchDevice,
                    ev.Timestamp, client.InputRoot, type, rawPointerPoint, ev.Modifiers, ev.Detail));
                return;
            }

            if (!client.IsEnabled || (_multitouch && ev.Emulated))
                return;
            
            if (ev.Type == XiEventType.XI_Motion)
            {
                Vector scrollDelta = default;
                foreach (var v in ev.Valuators)
                {
                    foreach (var scroller in _pointerDevice.Scrollers)
                    {
                        if (scroller.Number == v.Key)
                        {
                            var old = _pointerDevice.Valuators[scroller.Number].Value;
                            // Value was zero after reset, ignore the event and use it as a reference next time
                            if (old == 0)
                                continue;
                            var diff = (old - v.Value) / scroller.Increment;
                            if (scroller.ScrollType == XiScrollType.Horizontal)
                                scrollDelta = scrollDelta.WithX(scrollDelta.X + diff);
                            else
                                scrollDelta = scrollDelta.WithY(scrollDelta.Y + diff);

                        }
                    }


                }

                if (scrollDelta != default)
                    client.ScheduleXI2Input(new RawMouseWheelEventArgs(client.MouseDevice, ev.Timestamp,
                        client.InputRoot, ev.Position, scrollDelta, ev.Modifiers));
                if (_pointerDevice.HasMotion(ev))
                    client.ScheduleXI2Input(new RawPointerEventArgs(client.MouseDevice, ev.Timestamp, client.InputRoot,
                        RawPointerEventType.Move, ev.Position, ev.Modifiers));
            }

            if (ev.Type == XiEventType.XI_ButtonPress && ev.Button >= 4 && ev.Button <= 7 && !ev.Emulated)
            {
                var scrollDelta = ev.Button switch
                {
                    4 => new Vector(0, 1),
                    5 => new Vector(0, -1),
                    6 => new Vector(1, 0),
                    7 => new Vector(-1, 0),
                    _ => (Vector?)null
                };

                if (scrollDelta.HasValue)
                    client.ScheduleXI2Input(new RawMouseWheelEventArgs(client.MouseDevice, ev.Timestamp,
                        client.InputRoot, ev.Position, scrollDelta.Value, ev.Modifiers));
            }

            if (ev.Type == XiEventType.XI_ButtonPress || ev.Type == XiEventType.XI_ButtonRelease)
            {
                var down = ev.Type == XiEventType.XI_ButtonPress;
                var type = ev.Button switch
                {
                    1 => down ? RawPointerEventType.LeftButtonDown : RawPointerEventType.LeftButtonUp,
                    2 => down ? RawPointerEventType.MiddleButtonDown : RawPointerEventType.MiddleButtonUp,
                    3 => down ? RawPointerEventType.RightButtonDown : RawPointerEventType.RightButtonUp,
                    8 => down ? RawPointerEventType.XButton1Down : RawPointerEventType.XButton1Up,
                    9 => down ? RawPointerEventType.XButton2Down : RawPointerEventType.XButton2Up,
                    _ => (RawPointerEventType?)null
                };
                if (type.HasValue)
                    client.ScheduleXI2Input(new RawPointerEventArgs(client.MouseDevice, ev.Timestamp, client.InputRoot,
                        type.Value, ev.Position, ev.Modifiers));
            }
            
            _pointerDevice.UpdateValuators(ev.Valuators);
        }
    }

    internal unsafe class ParsedDeviceEvent
    {
        public XiEventType Type { get; }
        public RawInputModifiers Modifiers { get; }
        public ulong Timestamp { get; }
        public Point Position { get; }
        public Point RootPosition { get; }
        public int Button { get; set; }
        public int Detail { get; set; }
        public bool Emulated { get; set; }
        public Dictionary<int, double> Valuators { get; }

        public static RawInputModifiers ParseButtonState(int len, byte* buttons)
        {
            RawInputModifiers rv = default;
            if (len > 0)
            {
                if (XIMaskIsSet(buttons, 1))
                    rv |= RawInputModifiers.LeftMouseButton;
                if (XIMaskIsSet(buttons, 2))
                    rv |= RawInputModifiers.MiddleMouseButton;
                if (XIMaskIsSet(buttons, 3))
                    rv |= RawInputModifiers.RightMouseButton;
                if (len > 1)
                {
                    if (XIMaskIsSet(buttons, 8))
                        rv |= RawInputModifiers.XButton1MouseButton;
                    if (XIMaskIsSet(buttons, 9))
                        rv |= RawInputModifiers.XButton2MouseButton;
                }
            }
            return rv;
        }
        
        public ParsedDeviceEvent(XIDeviceEvent* ev)
        {
            Type = ev->evtype;
            Timestamp = (ulong)ev->time.ToInt64();
            var state = (XModifierMask)ev->mods.Effective;
            if (state.HasAllFlags(XModifierMask.ShiftMask))
                Modifiers |= RawInputModifiers.Shift;
            if (state.HasAllFlags(XModifierMask.ControlMask))
                Modifiers |= RawInputModifiers.Control;
            if (state.HasAllFlags(XModifierMask.Mod1Mask))
                Modifiers |= RawInputModifiers.Alt;
            if (state.HasAllFlags(XModifierMask.Mod4Mask))
                Modifiers |= RawInputModifiers.Meta;

            Modifiers |= ParseButtonState(ev->buttons.MaskLen, ev->buttons.Mask);

            Valuators = new Dictionary<int, double>();
            Position = new Point(ev->event_x, ev->event_y);
            RootPosition = new Point(ev->root_x, ev->root_y);
            var values = ev->valuators.Values;
            if(ev->valuators.Mask != null)
                for (var c = 0; c < ev->valuators.MaskLen * 8; c++)
                    if (XIMaskIsSet(ev->valuators.Mask, c))
                        Valuators[c] = *values++;
            
            if (Type == XiEventType.XI_ButtonPress || Type == XiEventType.XI_ButtonRelease)
                Button = ev->detail;
            Detail = ev->detail;
            Emulated = ev->flags.HasAllFlags(XiDeviceEventFlags.XIPointerEmulated);
        }
    }

    internal interface IXI2Client
    {
        bool IsEnabled { get; }
        IInputRoot InputRoot { get; }
        void ScheduleXI2Input(RawInputEventArgs args);
        IMouseDevice MouseDevice { get; }
        TouchDevice TouchDevice { get; }
    }
}
