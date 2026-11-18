using System;
using System.Windows.Forms;

namespace SteadyDim;

internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private static readonly uint TaskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
    private IntPtr _powerNotification;
    private bool _disposed;

    public MessageWindow()
    {
        var createParams = new CreateParams
        {
            Caption = "SteadyDim.MessageWindow",
            Style = 0,
            ExStyle = 0,
            Parent = IntPtr.Zero
        };

        CreateHandle(createParams);

        try
        {
            if (!Native.WTSRegisterSessionNotification(Handle, Native.NOTIFY_FOR_THIS_SESSION))
            {
                Log.Warn("WTSRegisterSessionNotification failed.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WTSRegisterSessionNotification threw: " + ex.Message);
        }

        try
        {
            var guid = Native.GUID_CONSOLE_DISPLAY_STATE;
            _powerNotification = Native.RegisterPowerSettingNotification(
                Handle,
                ref guid,
                Native.DEVICE_NOTIFY_WINDOW_HANDLE);

            if (_powerNotification == IntPtr.Zero)
            {
                Log.Warn("RegisterPowerSettingNotification failed.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("RegisterPowerSettingNotification threw: " + ex.Message);
        }
    }

    public event Action? DisplayChanged;
    public event Action? DeviceChanged;
    public event Action? PowerChanged;
    public event Action? SessionUnlocked;
    public event Action? TaskbarCreated;
    public event Action? ShowRequested;

    public void PostShow()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.PostMessage(Handle, (uint)Native.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
        }
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case Native.WM_APP_SHOW:
                ShowRequested?.Invoke();
                break;

            case Native.WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;

            case Native.WM_DEVICECHANGE:
                DeviceChanged?.Invoke();
                break;

            case Native.WM_POWERBROADCAST:
                var powerEvent = message.WParam.ToInt32();
                if (powerEvent is Native.PBT_APMRESUMEAUTOMATIC
                    or Native.PBT_APMRESUMESUSPEND
                    or Native.PBT_POWERSETTINGCHANGE)
                {
                    PowerChanged?.Invoke();
                }

                break;

            case Native.WM_WTSSESSION_CHANGE:
                if (message.WParam.ToInt32() == Native.WTS_SESSION_UNLOCK)
                {
                    SessionUnlocked?.Invoke();
                }

                break;

            default:
                if (TaskbarCreatedMessage != 0 && (uint)message.Msg == TaskbarCreatedMessage)
                {
                    TaskbarCreated?.Invoke();
                }

                break;
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_powerNotification != IntPtr.Zero)
            {
                Native.UnregisterPowerSettingNotification(_powerNotification);
                _powerNotification = IntPtr.Zero;
            }
        }
        catch
        {
            // Best effort.
        }

        try
        {
            Native.WTSUnRegisterSessionNotification(Handle);
        }
        catch
        {
            // Best effort.
        }

        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
