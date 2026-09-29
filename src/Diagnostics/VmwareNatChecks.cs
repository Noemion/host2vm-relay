namespace Host2VMRelay;

internal static class VmwareNatChecks
{
    public static void Run(string output, Action<bool, string> check)
    {
        const string nat = "ethernet0.connectionType = \"nat\"\r\n";
        const string setting = "vmnat.linkStatePropagation.disable = \"FALSE\"";
        string before = "# " + setting + "\r\n" + nat + setting + " # keep comment\r\n";
        string after = VmwareNatConfiguration.Prepare(before);
        check(after == "# " + setting + "\r\n" + nat + setting.Replace("FALSE", "TRUE") + " # keep comment\r\n",
            "NAT repair preserves comments, other settings and CRLF");
        check(VmwareNatConfiguration.Prepare(after) == after, "NAT repair is idempotent");
        check(VmwareNatConfiguration.Prepare(nat).EndsWith(" = \"TRUE\"\r\n"), "NAT repair adds missing setting");
        foreach (string invalid in new[] { "ethernet0.connectionType = \"bridged\"\n", nat + setting + "\n" + setting, nat + "vmnat.linkStatePropagation.disable = nope" })
        {
            bool rejected = false;
            try { VmwareNatConfiguration.Prepare(invalid); } catch (IOException) { rejected = true; }
            check(rejected, "NAT repair refuses unsupported or ambiguous configuration");
        }
        string folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, "vmware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "test.vmx");
        File.WriteAllText(path, before);
        var running = new VmwareNatConfiguration(path, () => true);
        bool blocked = false;
        try { running.Repair(); } catch (IOException) { blocked = true; }
        check(blocked && File.ReadAllText(path) == before, "NAT repair leaves running VM untouched");
        var offline = new VmwareNatConfiguration(path, () => false);
        File.WriteAllText(path, before + "checkpoint.vmState = \"saved.vmss\"\r\n");
        blocked = false;
        try { offline.Repair(); } catch (IOException) { blocked = true; }
        check(blocked, "NAT repair refuses suspended VM");
        File.WriteAllText(path, before);
        Directory.CreateDirectory(path + ".lck");
        string backup = offline.Repair();
        check(File.ReadAllText(backup) == before && File.ReadAllText(path) == after,
            "NAT repair backs up original and handles stale lock without deleting it");
        check(Directory.Exists(path + ".lck") && offline.Repair() == "", "NAT repair preserves lock directory and creates no redundant backup");
    }
}
