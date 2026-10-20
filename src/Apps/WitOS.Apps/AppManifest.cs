using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitOS.Apps;

/// <summary>
/// A version's manifest, witos-app.json (ADR 0027 decision 2): the catalog entry it was installed from, of which the
/// layer reads the identity, the version and the commands; the rest (the framework, the source and its pin, the native
/// part, the purpose) is kept as written.
/// </summary>
public sealed class AppManifest
{
    #region Constants

    private static readonly Regex IDENTITY = new(@"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)*$");
    private static readonly Regex VERSION = new(@"^[0-9]+(\.[0-9]+){1,3}$");
    private static readonly Regex COMMAND = new(@"^[a-z][a-z0-9-]*$");

    #endregion

    #region Constructors

    private AppManifest(string id, string version, IReadOnlyList<AppCommand> commands, string json)
    {
        Id = id;
        Version = version;
        Commands = commands;
        Json = json;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks a manifest: an identity and a version of the catalog's forms, and the commands, each a
    /// distinct name in lower case and an assembly within the version's directory.
    /// </summary>
    /// <param name="json">The manifest's text.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The manifest is not valid.</exception>
    public static AppManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"The manifest is no JSON: {error.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            var id = Text(root, "id");
            Require(id is not null && IDENTITY.IsMatch(id), "an identity");
            var version = Text(root, "version");
            Require(version is not null && VERSION.IsMatch(version), $"a version of two to four numbers for {id}");
            // An application without a command is reached by others (C2.5: the SDK, which dotnet finds through global.json).
            Require(root.TryGetProperty("commands", out var list) && list.ValueKind == JsonValueKind.Array, $"the commands of {id}");
            var commands = new List<AppCommand>();
            foreach (var item in list.EnumerateArray())
            {
                var name = Text(item, "name");
                var assembly = Text(item, "assembly");
                Require(name is not null && COMMAND.IsMatch(name), $"a command's name in lower case for {id}");
                Require(assembly is not null && assembly.EndsWith(".dll", StringComparison.Ordinal) && IsRelative(assembly),
                    $"the assembly of {name} within the directory of {id}");
                commands.Add(new AppCommand(name!, assembly!));
            }
            Require(commands.Select(command => command.Name).Distinct(StringComparer.Ordinal).Count() == commands.Count, $"distinct commands for {id}");
            return new AppManifest(id!, version!, commands, json);
        }
    }

    /// <summary>
    /// Whether a path stays within a directory: relative, with '/' and no empty, "." or ".." part.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it is relative and stays inside.</returns>
    public static bool IsRelative(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Require(bool condition, string what)
    {
        if (!condition)
            throw new InvalidDataException($"The manifest needs {what}.");
    }

    #endregion

    #region Properties

    /// <summary>
    /// The application's identity, its directory in the layer.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The version, its directory under the application's.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// The commands the version provides.
    /// </summary>
    public IReadOnlyList<AppCommand> Commands { get; }

    /// <summary>
    /// The manifest's text as written.
    /// </summary>
    public string Json { get; }

    #endregion
}

/// <summary>
/// A command of a base application: its name, the launcher's in the layer's bin directory, and the assembly it starts.
/// </summary>
/// <param name="Name">The command's name.</param>
/// <param name="Assembly">The assembly's path in the version's directory.</param>
public sealed record AppCommand(string Name, string Assembly);
