using System.Diagnostics;

namespace AbyssRemote.Tests;

/// <summary>ENABLE-LAN-FIREWALL.bat once glued the port onto the script's file name, so the
/// script never ran and no rule was added. These tests run the helper's real command line in
/// a folder whose name has spaces, with a stand-in script that only writes down the port it
/// was given. The firewall is never touched: the administrator check is replaced by a jump, and
/// the real script is not copied.</summary>
[TestClass]
public sealed class FirewallHelperTests
{
    [TestMethod]
    [DataRow("8555", "8555")]
    [DataRow("", "8444")] // no port given: the default
    public void TheHelperHandsThePortToTheScript(string argument, string expected)
    {
        string folder = Directory.CreateTempSubdirectory("abyss remote fw test ").FullName;
        try
        {
            string helper = PrepareHelper(folder);
            var (exitCode, _) = Run(helper, argument);

            string marker = Path.Combine(folder, "port.txt");
            Assert.IsTrue(File.Exists(marker), "The stand-in script did not run.");
            Assert.AreEqual(expected, File.ReadAllText(marker).Trim());
            Assert.AreEqual(0, exitCode);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void APortThatIsNotANumberIsRefusedBeforeAnythingRuns()
    {
        string folder = Directory.CreateTempSubdirectory("abyss remote fw test ").FullName;
        try
        {
            string helper = PrepareHelper(folder);
            var (exitCode, output) = Run(helper, "84a4");

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(output, "The port must be a number");
            Assert.IsFalse(File.Exists(Path.Combine(folder, "port.txt")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>Copies the real helper next to a stand-in script and replaces only its
    /// administrator check, so everything after the check is the shipped text.</summary>
    private static string PrepareHelper(string folder)
    {
        string text = File.ReadAllText(Path.Combine(RepositoryRoot(), "ENABLE-LAN-FIREWALL.bat"));
        const string check = "\"%SystemRoot%\\System32\\whoami.exe\" /groups | \"%SystemRoot%\\System32\\findstr.exe\" /c:\"S-1-16-12288\" /c:\"S-1-16-16384\" >nul 2>&1\r\nif not errorlevel 1 goto elevated\r\n";
        Assert.IsTrue(text.Contains(check), "The administrator check in ENABLE-LAN-FIREWALL.bat changed; update this test.");
        string helper = Path.Combine(folder, "ENABLE-LAN-FIREWALL.bat");
        File.WriteAllText(helper, text.Replace(check, "goto elevated\r\n"));

        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        File.WriteAllText(Path.Combine(folder, "scripts", "Firewall-Enable-LAN.ps1"),
            "param([int]$Port = 8444)\r\nSet-Content -LiteralPath (Join-Path $PSScriptRoot '..\\port.txt') -Value $Port\r\n");
        return helper;
    }

    private static (int ExitCode, string Output) Run(string helper, string argument)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("call");
        start.ArgumentList.Add(helper);
        if (argument.Length > 0) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close(); // "pause" returns at once when nobody can press a key
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(60_000), "The helper did not finish.");
        return (process.ExitCode, output);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ENABLE-LAN-FIREWALL.bat")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Could not find the repository folder above the test output folder.");
    }
}
