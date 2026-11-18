using System.Runtime.CompilerServices;
using System.Text;

namespace ContextMole.Documents;

/// <summary>
/// Makes the Windows code pages (1252 and the other legacy encodings that .NET only ships through
/// <see cref="CodePagesEncodingProvider"/>) available to everything in this assembly. MsgReader needs them as soon
/// as it opens an Outlook message with an RTF body, because its RTF font table creates Windows-1252 in a static
/// initializer, and a failed static initializer stays failed for the life of the process. Registering when the
/// module loads covers every host: the app that indexes and the broker that serves materialize_content, which
/// never constructs <see cref="DocumentExtractionRegistry"/>.
/// </summary>
internal static class LegacyTextEncodings
{
#pragma warning disable CA2255 // This library must register the provider before any of its own code uses MsgReader.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
