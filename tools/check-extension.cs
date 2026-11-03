// Проверка распакованного расширения по политике браузера — без запуска браузера.
// Нужен .NET 10 SDK. Запуск из корня репозитория:
//   dotnet run tools/check-extension.cs -- <папка с manifest.json> [policy.json]
// Без policy.json используется встроенная политика по умолчанию.
#:project ../src/Shadepane.Core/Shadepane.Core.csproj
#:property PublishAot=false
using Shadepane.Core.Extensions;
using Shadepane.Core.Policy;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: dotnet run tools/check-extension.cs -- <extension-folder> [policy.json]");
    return 2;
}

try
{
    var folder = args[0];
    var policy = args.Length == 2 ? BrowserPolicy.Parse(File.ReadAllText(args[1])) : BrowserPolicy.Default;
    var manifest = ManifestReader.ReadFromDirectory(folder);

    Console.WriteLine($"name:     {manifest.Name} {manifest.Version} (MV{manifest.ManifestVersion})");
    Console.WriteLine($"api:      {string.Join(", ", manifest.ApiPermissions)}");
    Console.WriteLine($"hosts:    {string.Join(", ", manifest.HostPermissions.Select(h => $"{h.Pattern} [{h.Source}]"))}");
    Console.WriteLine($"popup:    {manifest.PopupPath ?? "(нет — клик по иконке в WebView2 не поддерживается)"}");
    Console.WriteLine($"sha256:   {ExtensionDirectory.ComputeHash(folder)}");

    var result = ExtensionValidator.Validate(manifest, policy);
    Console.WriteLine(result.IsAllowed ? "RESULT:   allowed" : "RESULT:   denied");
    foreach (var v in result.Violations)
        Console.WriteLine($"  - [{v.Code}] {v.Message}");
    return result.IsAllowed ? 0 : 1;
}
catch (Exception ex) when (ex is ManifestException or ExtensionDirectoryException or PolicyException or IOException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
