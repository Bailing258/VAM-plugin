using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PatchDisableLoadCoP23 {
    private const string Marker = "[LoadPerf] P23: 纹理OS缓存预热:";

    private static bool IsCallTo(Instruction instruction, string typeName, string methodName) {
        MethodReference method = instruction.Operand as MethodReference;
        return method != null && method.DeclaringType.FullName == typeName && method.Name == methodName;
    }

    private static TypeDefinition FindLoadCoStateMachine(TypeDefinition controller) {
        return controller.NestedTypes.Single(type => type.Name.StartsWith("<LoadCo>d__", StringComparison.Ordinal));
    }

    public static int Main(string[] args) {
        if (args.Length < 2 || args.Length > 3) {
            Console.Error.WriteLine("Usage: PatchDisableLoadCoP23 <input Assembly-CSharp.dll> <output.dll> [VaM Managed dir]");
            return 2;
        }

        try {
            string inputPath = Path.GetFullPath(args[0]);
            string outputPath = Path.GetFullPath(args[1]);
            if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("Refusing to patch the input assembly in place");
            }
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath));
            if (args.Length == 3) resolver.AddSearchDirectory(Path.GetFullPath(args[2]));
            ReaderParameters readerParameters = new ReaderParameters { InMemory = true, AssemblyResolver = resolver };
            using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParameters)) {
                TypeDefinition controller = assembly.MainModule.Types.Single(type => type.FullName == "SuperController");
                MethodDefinition moveNext = FindLoadCoStateMachine(controller).Methods.Single(method => method.Name == "MoveNext");
                var instructions = moveNext.Body.Instructions;
                Instruction marker = instructions.Single(instruction => {
                    string value = instruction.Operand as string;
                    return value != null && value.StartsWith(Marker, StringComparison.Ordinal);
                });
                int markerIndex = instructions.IndexOf(marker);

                int previousPerfLog = -1;
                for (int i = markerIndex - 1; i >= 0; i--) {
                    if (IsCallTo(instructions[i], "SuperController", "PerfLog")) { previousPerfLog = i; break; }
                }
                if (previousPerfLog < 0 || previousPerfLog + 1 >= markerIndex) {
                    throw new InvalidOperationException("P23 entry anchor was not found");
                }
                Instruction timerCall = instructions[previousPerfLog + 1];
                Instruction timerStore = instructions[previousPerfLog + 2];
                if (!IsCallTo(timerCall, "UnityEngine.Time", "get_realtimeSinceStartup")
                    || (!timerStore.OpCode.Name.StartsWith("stloc", StringComparison.Ordinal))) {
                    throw new InvalidOperationException("Unexpected P23 timer prologue: " + timerCall + " / " + timerStore);
                }

                int p23PerfLog = -1;
                for (int i = markerIndex + 1; i < instructions.Count; i++) {
                    if (IsCallTo(instructions[i], "SuperController", "PerfLog")) { p23PerfLog = i; break; }
                }
                if (p23PerfLog < 0 || p23PerfLog + 1 >= instructions.Count) {
                    throw new InvalidOperationException("P23 exit anchor was not found");
                }
                Instruction exitBranch = instructions[p23PerfLog + 1];
                Instruction target = exitBranch.Operand as Instruction;
                if (target == null || (exitBranch.OpCode != OpCodes.Br && exitBranch.OpCode != OpCodes.Br_S)) {
                    throw new InvalidOperationException("Unexpected P23 exit branch: " + exitBranch);
                }

                ILProcessor il = moveNext.Body.GetILProcessor();
                Instruction gate = il.Create(OpCodes.Br, target);
                il.InsertBefore(timerCall, gate);
                int retargeted = 0;
                foreach (Instruction instruction in instructions) {
                    if (object.ReferenceEquals(instruction, gate)) continue;
                    if (object.ReferenceEquals(instruction.Operand, timerCall)) {
                        instruction.Operand = gate;
                        retargeted++;
                        continue;
                    }
                    Instruction[] targets = instruction.Operand as Instruction[];
                    if (targets == null) continue;
                    for (int i = 0; i < targets.Length; i++) {
                        if (!object.ReferenceEquals(targets[i], timerCall)) continue;
                        targets[i] = gate;
                        retargeted++;
                    }
                }
                foreach (ExceptionHandler handler in moveNext.Body.ExceptionHandlers) {
                    if (object.ReferenceEquals(handler.TryStart, timerCall)) { handler.TryStart = gate; retargeted++; }
                    if (object.ReferenceEquals(handler.TryEnd, timerCall)) { handler.TryEnd = gate; retargeted++; }
                    if (object.ReferenceEquals(handler.HandlerStart, timerCall)) { handler.HandlerStart = gate; retargeted++; }
                    if (object.ReferenceEquals(handler.HandlerEnd, timerCall)) { handler.HandlerEnd = gate; retargeted++; }
                    if (object.ReferenceEquals(handler.FilterStart, timerCall)) { handler.FilterStart = gate; retargeted++; }
                }
                if (retargeted == 0) throw new InvalidOperationException("No incoming P23 branches were retargeted");
                assembly.Write(outputPath);
                Console.WriteLine("PATCHED method=" + moveNext.FullName);
                Console.WriteLine("P23 entry has a new branch; incomingTargetsRetargeted=" + retargeted);
            }
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }
}
