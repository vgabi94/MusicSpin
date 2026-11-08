using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace MusicSpin.Tests.Shared;

/// <summary>
/// Reads a built assembly's metadata. Absence of the marker type and of its string literals is what shows the calls
/// were not compiled, which an empty activity list does not.
/// </summary>
internal static class MarkerMetadata
{
    public static bool HasType(Assembly assembly, string namespaceName, string typeName)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            return false;
        }

        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Namespace) == namespaceName && reader.GetString(type.Name) == typeName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="text"/> occurs as UTF-16 (C# user-string literals) in the assembly file.</summary>
    public static bool HasUserString(Assembly assembly, string text)
    {
        var bytes = File.ReadAllBytes(assembly.Location);
        var needle = Encoding.Unicode.GetBytes(text);
        return IndexOf(bytes, needle) >= 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
