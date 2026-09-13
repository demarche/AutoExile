// Read-only IL inspection: never load or execute the inspected plugin/host.
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

using var stream = File.Open(args[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();
var ops = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode))
    .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
string Name(EntityHandle handle) => handle.Kind switch
{
    HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)handle),
    HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
    HandleKind.MemberReference => Name(reader.GetMemberReference((MemberReferenceHandle)handle).Parent) + "." + reader.GetString(reader.GetMemberReference((MemberReferenceHandle)handle).Name),
    HandleKind.MethodDefinition => TypeName(reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType()) + "." + reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
    HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
    HandleKind.MethodSpecification => Name(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
    _ => handle.Kind.ToString()
};
string TypeName(TypeDefinitionHandle h)
{
    var t = reader.GetTypeDefinition(h);
    var parent = t.GetDeclaringType();
    return parent.IsNil ? reader.GetString(t.Namespace) + "." + reader.GetString(t.Name) : TypeName(parent) + "+" + reader.GetString(t.Name);
}
foreach (var typeHandle in reader.TypeDefinitions)
{
    var typeName = TypeName(typeHandle);
    if (!Regex.IsMatch(typeName, args.Length > 1 ? args[1] : ".*")) continue;
    foreach (var methodHandle in reader.GetTypeDefinition(typeHandle).GetMethods())
    {
        var method = reader.GetMethodDefinition(methodHandle);
        var methodName = reader.GetString(method.Name);
        if (!Regex.IsMatch(methodName, args.Length > 2 ? args[2] : ".*")) continue;
        if (method.RelativeVirtualAddress == 0) continue;
        Console.WriteLine($"METHOD {typeName}.{methodName}");
        var body = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        for (var index = 0; index < body.Length;)
        {
            var offset = index;
            ushort value = body[index++];
            if (value == 0xfe) value = (ushort)(0xfe00 | body[index++]);
            var op = ops[value];
            var length = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + BitConverter.ToInt32(body, index) * 4,
                _ => 4
            };
            string operand = "";
            if (length == 4)
            {
                var token = BitConverter.ToInt32(body, index);
                try
                {
                    operand = op.OperandType switch
                    {
                        OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok => Name(MetadataTokens.EntityHandle(token)),
                        OperandType.InlineString => "\"" + reader.GetUserString(MetadataTokens.UserStringHandle(token)) + "\"",
                        OperandType.InlineBrTarget => $"IL_{index + 4 + token:X4}",
                        _ => token.ToString()
                    };
                }
                catch { operand = $"0x{token:X8}"; }
            }
            else if (length == 1) operand = op.OperandType == OperandType.ShortInlineBrTarget ? $"IL_{index + 1 + (sbyte)body[index]:X4}" : body[index].ToString();
            Console.WriteLine($"  IL_{offset:X4} {op.Name} {operand}");
            index += length;
        }
    }
}
