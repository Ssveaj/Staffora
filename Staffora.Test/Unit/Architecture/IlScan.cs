using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Staffora.Test.Unit.Architecture;

internal static class IlScan
{
    private static readonly Dictionary<ushort, OpCode> opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    public static IEnumerable<MethodBase> DirectReferences(MethodBase method) =>
        Instructions(method).Select(instruction => instruction.Operand).OfType<MethodBase>();

    public static IReadOnlyList<MethodBase> Reach(MethodBase method, bool followOwnType = false) =>
        ReachAll([method], referenced => followOwnType && referenced.DeclaringType == method.DeclaringType && !referenced.IsPublic ? [referenced] : []);

    public static IReadOnlyList<MethodBase> ReachAll(IEnumerable<MethodBase> roots, Func<MethodBase, IEnumerable<MethodBase>> follow)
    {
        var seen = new HashSet<MethodBase>();
        var found = new List<MethodBase>();
        var pending = new Queue<MethodBase>(roots);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var body in BodiesOf(current))
            {
                foreach (var referenced in DirectReferences(body))
                {
                    found.Add(referenced);

                    if (IsCompilerGenerated(referenced))
                    {
                        pending.Enqueue(referenced);
                    }
                    else
                    {
                        foreach (var next in follow(referenced))
                        {
                            pending.Enqueue(next);
                        }
                    }
                }
            }
        }

        return found;
    }

    public static IReadOnlyList<(MethodBase In, OpCode Code, MethodBase Referenced)> ReachAllReferences(IEnumerable<MethodBase> roots, Func<MethodBase, OpCode, MethodBase, IEnumerable<MethodBase>> follow)
    {
        var seen = new HashSet<MethodBase>();
        var found = new List<(MethodBase, OpCode, MethodBase)>();
        var pending = new Queue<MethodBase>(roots);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var body in BodiesOf(current))
            {
                foreach (var (code, operand) in Instructions(body))
                {
                    if (operand is not MethodBase referenced)
                    {
                        continue;
                    }

                    found.Add((current, code, referenced));
                    if (IsCompilerGenerated(referenced))
                    {
                        pending.Enqueue(referenced);
                    }
                    else
                    {
                        foreach (var next in follow(current, code, referenced))
                        {
                            pending.Enqueue(next);
                        }
                    }
                }
            }
        }

        return found;
    }

    public static bool IsGenerated(MethodBase method) => IsCompilerGenerated(method);

    public static (IReadOnlyList<(MethodBase In, object Operand)> Operands, IReadOnlyList<MethodBase> Bodies) Walk(IEnumerable<MethodBase> roots, Func<object, IEnumerable<MethodBase>> expand)
    {
        var seen = new HashSet<MethodBase>();
        var found = new List<(MethodBase, object)>();
        var bodies = new List<MethodBase>();
        var pending = new Queue<MethodBase>(roots);
        while (pending.TryDequeue(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var body in BodiesOf(current))
            {
                bodies.Add(body);
                var locals = body.GetMethodBody()?.LocalVariables;
                Type? constrained = null;
                LocalSlot? loaded = null;
                foreach (var (code, read) in Instructions(body))
                {
                    var operand = constrained is not null && code == OpCodes.Callvirt && read is MethodInfo called ? ConstrainedTarget(constrained, called) : read;

                    var expanded = constrained is null && code == OpCodes.Callvirt && operand is MethodInfo { IsVirtual: true } virtualCall
                        && loaded is { IsStore: false } slot && locals is { } declared && slot.Index < declared.Count
                        ? new ReceiverCall(virtualCall, declared[slot.Index].LocalType)
                        : operand;
                    constrained = code == OpCodes.Constrained ? read as Type : null;
                    loaded = read as LocalSlot;
                    if (operand is null or LocalSlot)
                    {
                        continue;
                    }

                    found.Add((current, operand));
                    if (operand is MethodBase referenced && IsCompilerGenerated(referenced))
                    {
                        pending.Enqueue(referenced);
                    }

                    foreach (var next in expand(expanded!))
                    {
                        pending.Enqueue(next);
                    }
                }
            }
        }

        return (found, bodies);
    }

    public static MethodBase ConstrainedTarget(Type constrained, MethodInfo called)
    {
        ArgumentNullException.ThrowIfNull(constrained);
        ArgumentNullException.ThrowIfNull(called);
        if (!called.IsVirtual || constrained.IsGenericParameter || !(constrained.IsValueType || constrained.IsSealed))
        {
            return called;
        }

        try
        {
            if (called.DeclaringType is { IsInterface: true } contract)
            {
                var map = constrained.GetInterfaceMap(contract);
                var index = Array.IndexOf(map.InterfaceMethods, called);
                if (index >= 0)
                {
                    return map.TargetMethods[index];
                }
            }
            else if (constrained.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(candidate => candidate.GetBaseDefinition() == called.GetBaseDefinition()) is { } overriding)
            {
                return overriding;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
        }

        var own = constrained.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(candidate => (candidate.Name == called.Name || candidate.Name.EndsWith("." + called.Name, StringComparison.Ordinal)) && candidate.GetParameters().Length == called.GetParameters().Length)
            .ToList();
        return own.Count == 1 ? own[0] : called;
    }

    public static IReadOnlyList<(MethodBase Called, IReadOnlyList<string?> Literals)> LiteralArguments(MethodBase method)
    {
        var code = Decode(method, out var decodable);
        var calls = new List<(MethodBase, IReadOnlyList<string?>)>();
        if (code.Count == 0)
        {
            return calls;
        }

        var unread = code.Where(IsCall)
            .Select(instruction => ((MethodBase)instruction.Operand!, (IReadOnlyList<string?>)new string?[ArgumentCount((MethodBase)instruction.Operand!, instruction.Code)]))
            .ToList();
        if (!decodable)
        {
            return unread;
        }

        var index = new Dictionary<int, int>();
        for (var position = 0; position < code.Count; position++)
        {
            index[code[position].Offset] = position;
        }

        var before = new int[]?[code.Count];
        var pending = new Queue<int>();
        void Reach(int position, int[] stack)
        {
            if (before[position] is not { } known)
            {
                before[position] = stack;
                pending.Enqueue(position);
                return;
            }

            if (known.Length != stack.Length)
            {
                throw new InvalidOperationException("The stack disagrees where paths meet.");
            }

            var changed = false;
            for (var slot = 0; slot < known.Length; slot++)
            {
                if (known[slot] != stack[slot] && known[slot] != -1)
                {
                    known[slot] = -1;
                    changed = true;
                }
            }

            if (changed)
            {
                pending.Enqueue(position);
            }
        }

        try
        {
            Reach(0, []);
            foreach (var clause in method.GetMethodBody()?.ExceptionHandlingClauses ?? [])
            {
                var withException = clause.Flags is ExceptionHandlingClauseOptions.Clause or ExceptionHandlingClauseOptions.Filter;
                Reach(index[clause.HandlerOffset], withException ? [-1] : []);
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                {
                    Reach(index[clause.FilterOffset], [-1]);
                }
            }

            while (pending.TryDequeue(out var position))
            {
                var instruction = code[position];
                var stack = new List<int>(before[position]!);
                var (pops, pushes) = StackEffect(instruction, stack.Count);
                if (pops > stack.Count)
                {
                    throw new InvalidOperationException("The stack runs dry.");
                }

                var produced = instruction.Code == OpCodes.Dup ? stack[^1] : position;
                stack.RemoveRange(stack.Count - pops, pops);
                for (var push = 0; push < pushes; push++)
                {
                    stack.Add(produced);
                }

                if (IsLeave(instruction.Code) || instruction.Code == OpCodes.Endfinally)
                {
                    stack.Clear();
                }

                foreach (var target in instruction.Targets)
                {
                    Reach(index[target], [.. stack]);
                }

                if (!EndsFlow(instruction.Code) && position + 1 < code.Count)
                {
                    Reach(position + 1, [.. stack]);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            return unread;
        }

        for (var position = 0; position < code.Count; position++)
        {
            if (!IsCall(code[position]) || code[position].Operand is not { } called)
            {
                continue;
            }

            var count = ArgumentCount(called, code[position].Code);
            var literals = new string?[count];
            if (before[position] is { } stack)
            {
                for (var argument = 0; argument < count && argument < stack.Length; argument++)
                {
                    var producer = stack[stack.Length - count + argument];
                    literals[argument] = producer >= 0 && code[producer].Code == OpCodes.Ldstr ? code[producer].Literal : null;
                }
            }

            calls.Add((called, literals));
        }

        return calls;
    }

    private static int ArgumentCount(MethodBase called, OpCode code) =>
        called.GetParameters().Length + (code != OpCodes.Newobj && !called.IsStatic ? 1 : 0);

    private static (int Pops, int Pushes) StackEffect(DecodedInstruction instruction, int depth)
    {
        var code = instruction.Code;
        if (code == OpCodes.Calli)
        {
            throw new InvalidOperationException("An indirect call names no signature this reads.");
        }

        if (instruction.Operand is MethodBase called && (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj))
        {
            var returns = code == OpCodes.Newobj || (called is MethodInfo info && info.ReturnType != typeof(void));
            return (ArgumentCount(called, code), returns ? 1 : 0);
        }

        if (code == OpCodes.Ret)
        {
            return (depth, 0);
        }

        var pops = code.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
                or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
            _ => throw new InvalidOperationException($"{code} takes a number of values this does not read."),
        };
        var pushes = code.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => throw new InvalidOperationException($"{code} leaves a number of values this does not read."),
        };
        return (pops, pushes);
    }

    private static bool IsCall(DecodedInstruction instruction) =>
        instruction.Operand is not null && (instruction.Code == OpCodes.Call || instruction.Code == OpCodes.Callvirt || instruction.Code == OpCodes.Newobj);

    private static bool IsLeave(OpCode code) => code == OpCodes.Leave || code == OpCodes.Leave_S;

    private static bool EndsFlow(OpCode code) =>
        code == OpCodes.Br || code == OpCodes.Br_S || IsLeave(code) || code == OpCodes.Ret || code == OpCodes.Throw || code == OpCodes.Rethrow
        || code == OpCodes.Endfinally || code == OpCodes.Endfilter || code == OpCodes.Jmp;

    private sealed record DecodedInstruction(int Offset, OpCode Code, MethodBase? Operand, string? Literal, IReadOnlyList<int> Targets);

    private static List<DecodedInstruction> Decode(MethodBase method, out bool decodable)
    {
        decodable = true;
        var decoded = new List<DecodedInstruction>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return decoded;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var offset = position;
            OpCode code;
            if (il[position] == 0xFE)
            {
                code = opCodes[(ushort)(0xFE00 | il[position + 1])];
                position += 2;
            }
            else
            {
                code = opCodes[il[position]];
                position += 1;
            }

            MethodBase? operand = null;
            string? literal = null;
            var targets = new List<int>();
            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                    try
                    {
                        operand = method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                        decodable = false;
                    }

                    position += 4;
                    break;
                case OperandType.InlineString:
                    literal = method.Module.ResolveString(BitConverter.ToInt32(il, position));
                    position += 4;
                    break;
                case OperandType.ShortInlineBrTarget:
                    targets.Add(position + 1 + (sbyte)il[position]);
                    position += 1;
                    break;
                case OperandType.InlineBrTarget:
                    targets.Add(position + 4 + BitConverter.ToInt32(il, position));
                    position += 4;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, position);
                    var end = position + 4 + (4 * count);
                    for (var branch = 0; branch < count; branch++)
                    {
                        targets.Add(end + BitConverter.ToInt32(il, position + 4 + (4 * branch)));
                    }

                    position = end;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineI:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                default:
                    position += 4;
                    break;
            }

            decoded.Add(new DecodedInstruction(offset, code, operand, literal, targets));
        }

        return decoded;
    }

    public static IReadOnlyList<MethodBase> Sequence(MethodBase method, Func<MethodBase, bool> inline) =>
        [.. Flow(method, inline).OfType<FlowCall>().Select(call => call.Method)];

    public static IReadOnlyList<FlowEvent> Flow(MethodBase method, Func<MethodBase, bool> inline)
    {
        var events = new List<FlowEvent>();
        Append(method, inline, events, new HashSet<MethodBase>());
        return events;
    }

    private static void Append(MethodBase method, Func<MethodBase, bool> inline, List<FlowEvent> events, HashSet<MethodBase> reading)
    {
        if (!reading.Add(method))
        {
            return;
        }

        foreach (var body in BodiesOf(method))
        {
            var locals = body.GetMethodBody()?.LocalVariables;
            foreach (var (code, operand) in Instructions(body))
            {
                switch (operand)
                {
                    case MethodBase referenced when IsCompilerGenerated(referenced) || inline(referenced):
                        Append(referenced, inline, events, reading);
                        break;
                    case MethodBase referenced:
                        events.Add(new FlowCall(referenced));
                        break;
                    case FieldInfo field when code == OpCodes.Ldfld || code == OpCodes.Ldsfld:
                        events.Add(new FlowLoad(field, field.FieldType));
                        break;
                    case FieldInfo field when code == OpCodes.Stfld || code == OpCodes.Stsfld:
                        events.Add(new FlowStore(field, field.FieldType));
                        break;
                    case LocalSlot slot when locals is not null && slot.Index < locals.Count:
                        var local = (body, slot.Index);
                        events.Add(slot.IsStore ? new FlowStore(local, locals[slot.Index].LocalType) : new FlowLoad(local, locals[slot.Index].LocalType));
                        break;
                }
            }
        }

        reading.Remove(method);
    }

    private static IEnumerable<(OpCode Code, object? Operand)> Instructions(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            OpCode code;
            if (il[position] == 0xFE)
            {
                code = opCodes[(ushort)(0xFE00 | il[position + 1])];
                position += 2;
            }
            else
            {
                code = opCodes[il[position]];
                position += 1;
            }

            object? operand = null;
            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                    var token = BitConverter.ToInt32(il, position);
                    position += 4;
                    try
                    {
                        operand = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                    }

                    break;
                case OperandType.InlineField:
                    var fieldToken = BitConverter.ToInt32(il, position);
                    position += 4;
                    try
                    {
                        operand = method.Module.ResolveField(fieldToken, typeArguments, methodArguments);
                    }
                    catch (ArgumentException)
                    {
                    }

                    break;
                case OperandType.InlineString:
                    operand = new StringOperand(method.Module.ResolveString(BitConverter.ToInt32(il, position)));
                    position += 4;
                    break;
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    var typeToken = BitConverter.ToInt32(il, position);
                    position += 4;
                    try
                    {
                        operand = code.OperandType == OperandType.InlineType
                            ? (object)method.Module.ResolveType(typeToken, typeArguments, methodArguments)
                            : method.Module.ResolveMember(typeToken, typeArguments, methodArguments) is { } member ? new TokenOperand(member) : null;
                    }
                    catch (ArgumentException)
                    {
                    }

                    break;
                case OperandType.InlineNone:
                    operand = ShortFormLocal(code);
                    break;
                case OperandType.ShortInlineVar:
                    operand = IsLocalAccess(code) ? new LocalSlot(il[position], IsStore(code)) : null;
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    operand = IsLocalAccess(code) ? new LocalSlot(BitConverter.ToUInt16(il, position), IsStore(code)) : null;
                    position += 2;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                    position += 1;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (4 * BitConverter.ToInt32(il, position));
                    break;
                default:
                    position += 4;
                    break;
            }

            yield return (code, operand);
        }
    }

    private static LocalSlot? ShortFormLocal(OpCode code)
    {
        OpCode[] loads = [OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3];
        OpCode[] stores = [OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3];
        var load = Array.IndexOf(loads, code);
        if (load >= 0)
        {
            return new LocalSlot(load, IsStore: false);
        }

        var store = Array.IndexOf(stores, code);
        return store >= 0 ? new LocalSlot(store, IsStore: true) : null;
    }

    private static bool IsLocalAccess(OpCode code) => code == OpCodes.Ldloc_S || code == OpCodes.Ldloc || code == OpCodes.Stloc_S || code == OpCodes.Stloc;

    private static bool IsStore(OpCode code) => code == OpCodes.Stloc_S || code == OpCodes.Stloc;

    private static IEnumerable<MethodBase> BodiesOf(MethodBase method)
    {
        yield return method;
        if (method.GetCustomAttribute<StateMachineAttribute>() is { StateMachineType: { } machine })
        {
            var moveNext = machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (moveNext is not null)
            {
                yield return moveNext;
            }
        }
    }

    private static bool IsCompilerGenerated(MethodBase method) =>
        method.Name.Contains('<', StringComparison.Ordinal)
        || method.DeclaringType is { } type && (type.Name.Contains('<', StringComparison.Ordinal) || type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null);

    private sealed record LocalSlot(int Index, bool IsStore);

    public sealed record ReceiverCall(MethodInfo Method, Type Receiver)
    {
        public bool MayBe(Type candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (!Receiver.ContainsGenericParameters && !candidate.ContainsGenericParameters)
            {
                return Receiver.IsAssignableFrom(candidate);
            }

            var wanted = Receiver.IsGenericType ? Receiver.GetGenericTypeDefinition() : Receiver;
            var related = new List<Type>();
            for (var type = candidate; type is not null; type = type.BaseType)
            {
                related.Add(type);
            }

            related.AddRange(candidate.GetInterfaces());
            return related.Any(type => (type.IsGenericType ? type.GetGenericTypeDefinition() : type) == wanted || wanted.IsGenericParameter);
        }
    }
}

internal sealed record StringOperand(string Value);

internal sealed record TokenOperand(MemberInfo Member);

internal abstract record FlowEvent;

internal sealed record FlowCall(MethodBase Method) : FlowEvent;

internal sealed record FlowLoad(object Slot, Type Type) : FlowEvent;

internal sealed record FlowStore(object Slot, Type Type) : FlowEvent;
