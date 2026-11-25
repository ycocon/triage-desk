using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace TriageDesk.Api.Operations;

public sealed class ProtectionKeyRecord
{
    public Guid Id { get; set; }
    public required string Xml { get; set; }
}

internal sealed class ProtectionKeyStore(NpgsqlDataSource database) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var command = database.CreateCommand("""SELECT "Xml" FROM "ProtectionKeys" """);
        command.CommandTimeout = 5;
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read()) elements.Add(XElement.Parse(reader.GetString(0)));
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var command = database.CreateCommand("""INSERT INTO "ProtectionKeys" ("Id", "Xml") VALUES ($1, $2)""");
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
