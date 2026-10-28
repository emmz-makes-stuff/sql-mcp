namespace SqlMcp.Introspection;

/// <summary>
/// One thing in a database's catalog, as the inventory and the script generator both name it. <see cref="Type"/> is a
/// wire name: one of <see cref="ObjectTypes.WireNames"/>, or one of <see cref="CatalogItemTypes"/> for the kinds agents
/// can't list. <see cref="Schema"/> is null for anything that isn't schema-scoped, such as a user or a database trigger.
/// </summary>
internal sealed record CatalogItem(string? Schema, string Name, string Type);

/// <summary>
/// The wire names of the catalog kinds that aren't <see cref="ObjectType"/>s, as SQL Server's catalog names them. A
/// top-level object of any other kind, such as a rule or a service queue, is named by its lower-cased <c>type_desc</c>.
/// </summary>
internal static class CatalogItemTypes
{
    public const string Schema = "schema";
    public const string PartitionFunction = "partition_function";
    public const string PartitionScheme = "partition_scheme";
    public const string XmlSchemaCollection = "xml_schema_collection";
    public const string Assembly = "assembly";
    public const string FullTextCatalog = "fulltext_catalog";
    public const string User = "user";
    public const string Role = "role";
    public const string ApplicationRole = "application_role";

    /// <summary>The explicit permissions one principal holds, named by the principal (spec: database users).</summary>
    public const string Permissions = "permissions";

    public const string Certificate = "certificate";
    public const string AsymmetricKey = "asymmetric_key";
    public const string SymmetricKey = "symmetric_key";
    public const string DatabaseScopedCredential = "database_scoped_credential";
}

/// <summary>What an inventory entry is, which decides why it is omitted when the script leaves it out.</summary>
internal enum InventoryClass
{
    /// <summary>Something the generator is expected to script.</summary>
    Scriptable,

    /// <summary>A user or role, or the permissions one holds: never scripted (spec: database users).</summary>
    SecurityPrincipal,

    /// <summary>A certificate, key or credential: never scripted, since it holds or guards secrets.</summary>
    SecurityObject,
}

/// <summary>
/// One entry in a database's inventory. <see cref="Encrypted"/> is true for a module created <c>WITH ENCRYPTION</c>.
/// <see cref="Parent"/> is a DML trigger's table or view, and null for everything else. <see cref="ParentEncrypted"/>
/// says that view is encrypted, so the trigger goes with it, whether or not the view is in the same inventory.
/// </summary>
internal sealed record InventoryItem(CatalogItem Item, InventoryClass Class, bool Encrypted = false)
{
    public CatalogItem? Parent { get; init; }

    public bool ParentEncrypted { get; init; }
}

/// <summary>Something the script leaves out, and why (design I5). <see cref="Reason"/> is one of the <see cref="Completeness"/> reasons.</summary>
internal sealed record OmittedItem(CatalogItem Item, string Reason);

/// <summary>
/// The completeness check (design I5, spec: nothing is left out silently): everything in the inventory is either in the
/// script or omitted with a reason.
/// </summary>
internal static class Completeness
{
    public const string Encrypted = "encrypted";
    public const string SecurityPrincipal = "security principal";
    public const string SecurityObject = "security object";
    public const string NotScripted = "not scripted by the generator";

    /// <summary>A DML trigger whose view is encrypted, and so left out with it (code review, 2026-10-28).</summary>
    public const string OnEncryptedView = "on an encrypted view";

    /// <summary>
    /// The inventory entries missing from <paramref name="scripted"/>, in inventory order. Scripted items that aren't
    /// in the inventory don't matter here: they are counted, not checked.
    /// </summary>
    public static IReadOnlyList<OmittedItem> Omitted(IReadOnlyList<InventoryItem> inventory, IReadOnlyCollection<CatalogItem> scripted)
    {
        var done = scripted as IReadOnlySet<CatalogItem> ?? scripted.ToHashSet();
        return [.. inventory.Where(entry => !done.Contains(entry.Item)).Select(entry => new OmittedItem(entry.Item, Reason(entry)))];
    }

    private static string Reason(InventoryItem entry) => entry switch
    {
        { Encrypted: true } => Encrypted,
        { Parent: not null, ParentEncrypted: true } => OnEncryptedView,
        { Class: InventoryClass.SecurityPrincipal } => SecurityPrincipal,
        { Class: InventoryClass.SecurityObject } => SecurityObject,
        _ => NotScripted,
    };
}
