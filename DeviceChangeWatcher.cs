using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoiceAec;

// Implements WASAPI's endpoint callback. WASAPI invokes it on its own worker
// thread for every add/remove/state/default/property change; the panel only
// needs the fact that *something* changed, so all five funnel into one event.
internal sealed class DeviceChangeWatcher : IMMNotificationClient
{
    public event Action? Changed;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Fire();
    public void OnDeviceAdded(string deviceId) => Fire();
    public void OnDeviceRemoved(string deviceId) => Fire();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Fire();
    public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey) => Fire();

    private void Fire()
    {
        // Callbacks run on a WASAPI thread; a handler exception must not escape
        // back through the COM boundary, so swallow it here.
        try { Changed?.Invoke(); }
        catch { }
    }
}
