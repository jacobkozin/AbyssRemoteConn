namespace AbyssRemote.Host.App;

/// <summary><c>AbyssRemote.exe --console</c>: the classic text screen instead of the window.
/// It shows the addresses and the pairing code, prints what happens, and takes three keys:
/// P = new pairing code, R = revoke all devices, Q = quit.</summary>
internal static class ConsoleScreen
{
    public static async Task<int> RunAsync(HostRuntime runtime, HostRunOptions options, string? settingsNotice)
    {
        // Lines that arrive while the server is still starting are held back, so the screen
        // always begins with the banner.
        var early = new List<NoticeLine>();
        bool bannerShown = false;
        runtime.NoticeAdded += line =>
        {
            lock (early)
            {
                if (!bannerShown) { early.Add(line); return; }
            }
            Print(line);
        };

        var status = await runtime.StartAsync(options);
        if (status.Running is not { } running)
        {
            Console.Error.WriteLine(status.Problem!.Message);
            if (status.Problem.Kind == StartProblemKind.PortInUse)
                Console.Error.WriteLine("Close that program, or start Abyss Remote on another port, for example:  START-LOCAL-ONLY.bat --port 8555");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("===============================================");
        Console.WriteLine($"  ABYSS REMOTE {AppInfo.Version}");
        Console.WriteLine("===============================================");
        Console.WriteLine($"Mode        : {(running.Lan ? "LAN - devices on your private network" : "This PC only (loopback)")}");
        Console.WriteLine($"Open        : {running.LocalUrl}");
        foreach (string url in running.LanUrls) Console.WriteLine($"              {url}");
        Console.WriteLine($"Pair code   : {runtime.NewPairingCode().Digits}   (valid 10 minutes, works once)");
        Console.WriteLine($"Server cert : SHA-256 {running.ServerFingerprint}");
        Console.WriteLine($"Root CA     : SHA-256 {running.RootFingerprint}");
        Console.WriteLine($"Root CA file: {running.RootCertificateFile}");
        Console.WriteLine("Keys        : P = new pairing code   R = revoke ALL devices   Q = quit");
        Console.WriteLine("Never port-forward this host to the internet.");
        Console.WriteLine();
        if (settingsNotice is not null) Print(new NoticeLine(DateTimeOffset.Now, settingsNotice));
        lock (early)
        {
            foreach (var line in early) Print(line);
            bannerShown = true;
        }

        // Stopped is reached by pressing Q, and also when Ctrl+C stops the web server.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CheckStopped() { if (runtime.Status.Phase == HostPhase.Stopped) stopped.TrySetResult(); }
        runtime.StatusChanged += CheckStopped;
        CheckStopped(); // in case it already happened

        _ = Task.Run(() =>
        {
            while (true)
            {
                ConsoleKey key;
                try { key = Console.ReadKey(intercept: true).Key; }
                catch (InvalidOperationException) { return; } // no interactive console (input redirected)
                if (key == ConsoleKey.P)
                    Print(new NoticeLine(DateTimeOffset.Now, $"New pairing code: {runtime.NewPairingCode().Digits}   (valid 10 minutes, works once)"));
                if (key == ConsoleKey.R) runtime.RevokeAllDevices();
                if (key == ConsoleKey.Q)
                {
                    _ = runtime.StopAsync();
                    return;
                }
            }
        });
        await stopped.Task;
        return 0;
    }

    private static void Print(NoticeLine line) => Console.WriteLine($"[{line.Time.ToLocalTime():HH:mm:ss}] {line.Message}");
}
