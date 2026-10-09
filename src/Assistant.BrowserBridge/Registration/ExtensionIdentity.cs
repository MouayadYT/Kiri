namespace Assistant.BrowserBridge.Registration;

/// <summary>
/// The id of the Assistant's browser extension. A browser gives an unpacked extension an id made from the folder it was loaded from, which
/// would differ on every machine, but one whose manifest holds a <c>key</c> gets the id made from that public key. The key is in
/// <c>extension/manifest.json</c>; this is the id it gives (the first 16 bytes of the SHA-256 of the key's DER bytes, each hex digit written
/// as a letter from a to p). A test recomputes it from the manifest, so the two cannot drift apart. An extension published in a browser's
/// store gets an id of its own: it is added with <c>register --extension-id</c>.
/// </summary>
internal static class ExtensionIdentity
{
    /// <summary>The id of the extension in <c>src/Assistant.BrowserBridge/extension</c>.</summary>
    public const string Id = "cklcgaanpeplmbjmconamjjghmibhfok";
}
