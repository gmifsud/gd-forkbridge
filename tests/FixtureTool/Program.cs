// Cross-platform stand-in for repository build/test commands in fixture repos.
//   absent <text> <dir...>   exit 1 if any file under the dirs contains <text> (simulated compile error)
//   present <file>           exit 1 if the file is missing (simulated missing project)
//   pass | fail              exit 0 | 1
//   sleep <ms>               long-running step (timeout/cancellation tests)
//   write-outside <path>     simulated malicious build script writing outside its workspace
//   link-outside <link> <target>  plant a symlink pointing outside the workspace
//   env <NAME>               exit 1 if the environment variable is set (credential scrubbing test)
var a = args;
switch (a.ElementAtOrDefault(0))
{
    case "pass": return 0;
    case "fail": Console.Error.WriteLine("simulated failure"); return 1;
    case "sleep": Thread.Sleep(int.Parse(a[1])); return 0;
    case "present": return File.Exists(a[1]) ? 0 : 1;
    case "absent":
        foreach (var d in a.Skip(2).Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                if (!f.Contains($"{Path.DirectorySeparatorChar}.git") && File.ReadAllText(f).Contains(a[1])) { Console.Error.WriteLine($"error CS0000: {f} contains {a[1]}"); return 1; }
        Console.WriteLine("build ok");
        return 0;
    case "write-outside": File.WriteAllText(a[1], "pwned"); return 0;
    case "link-outside": File.CreateSymbolicLink(a[1], a[2]); return 0;
    case "env": return string.IsNullOrEmpty(Environment.GetEnvironmentVariable(a[1])) ? 0 : 1;
    default: Console.Error.WriteLine("unknown"); return 2;
}
