using VoiceAec;

internal static class StartupMicPinTests
{
    public static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS: " + name);
        }

        var calls = 0;
        var ready = false;
        var pin = new StartupMicPin(true, () => { calls++; return ready; });
        pin.TryPin(false, true);
        Check(calls == 0, "paused startup does not inspect or switch default devices");
        for (var i = 0; i < 100; i++) pin.TryPin(false, false);
        Check(calls == 30, "missing cable / failed pin stops periodic enumeration after the startup budget");
        ready = true;
        pin.TryPin(true, false);
        Check(calls == 31, "late cable notification retries even after startup polling stops");
        for (var i = 0; i < 100; i++) pin.TryPin(i % 2 == 0, false);
        Check(calls == 31, "success stops all checks including later default-device notifications");
        pin.SetEnabled(true);
        pin.TryPin(true, false);
        Check(calls == 31, "repeated enabled API requests do not rearm a completed pin");
        pin.SetEnabled(false);
        pin.TryPin(true, false);
        Check(!pin.Enabled && calls == 31, "disabled pin ignores device notifications");
        pin.SetEnabled(true);
        pin.TryPin(false, false);
        Check(pin.Enabled && calls == 32, "off then on resets completion and the exhausted poll budget");

        var concurrentCalls = 0;
        var concurrent = new StartupMicPin(true, () => { Interlocked.Increment(ref concurrentCalls); return true; });
        Parallel.For(0, 100, _ => concurrent.TryPin(true, false));
        Check(concurrentCalls == 1, "concurrent pin requests execute one successful device operation");

        var failures = 0;
        var throwing = new StartupMicPin(true, () => { failures++; throw new InvalidOperationException("COM unavailable"); });
        for (var i = 0; i < 100; i++)
        {
            try { throwing.TryPin(false, false); }
            catch (InvalidOperationException) { }
        }
        Check(failures == 30, "COM exceptions consume the retry budget and never cause endless polling");

        var alreadyDefault = 0;
        var correct = new StartupMicPin(true, () => { alreadyDefault++; return true; });
        correct.TryPin(false, false);
        correct.TryPin(true, false);
        Check(alreadyDefault == 1, "already-correct defaults finish the startup session immediately");
    }
}
