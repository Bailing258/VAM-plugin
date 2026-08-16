using System;
using System.IO;
using System.Reflection;

internal static class TestVaMLoadPerfFilter {
    private static readonly string BuildDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string PluginDir = Directory.GetParent(BuildDir.TrimEnd(Path.DirectorySeparatorChar)).FullName;
    private static readonly string Root = Path.GetFullPath(Path.Combine(PluginDir, @"..\..\.."));

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args) {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string[] dirs = {
            BuildDir,
            Path.Combine(Root, @"BepInEx\core"),
            Path.Combine(Root, @"VaM_Data\Managed")
        };
        foreach (string dir in dirs) {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static void Assert(MethodInfo filter, string message, bool expected) {
        bool actual = (bool)filter.Invoke(null, new object[] { message });
        if (actual != expected) throw new Exception("filter mismatch expected=" + expected + " actual=" + actual + " message=" + message);
    }

    private static int Main() {
        try {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            Assembly plugin = Assembly.LoadFrom(Path.Combine(BuildDir, "AllPackagesLinker.dll"));
            MethodInfo filter = plugin.GetType("AllPackagesLinkerBepInEx", true).GetMethod("IsVaMLoadPerfSummary", BindingFlags.Static | BindingFlags.NonPublic);
            if (filter == null) throw new MissingMethodException("IsVaMLoadPerfSummary");
            Assert(filter, "[LoadPerf] Phase-CheckHoldLoad: 等待248帧, 耗时=20304ms", true);
            Assert(filter, "[LoadPerf] ========== LoadCo 开始 [v1.1] ==========", true);
            Assert(filter, "[LoadPerf] P11-CheckHoldLoad首次进入: 共79个未完成flag:", false);
            Assert(filter, "[LoadPerf]   Atom恢复 #12", false);
            Assert(filter, "[LoadPerf]   LateRestore #1 'Person' type=Person 耗时=12345ms", true);
            Assert(filter, "not a load profile message", false);
            Console.WriteLine("PASS VaM LoadPerf filter");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine("FAIL " + error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }
}
