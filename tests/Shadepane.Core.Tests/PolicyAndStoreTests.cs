using Shadepane.Core.Audit;
using Shadepane.Core.Extensions;
using Shadepane.Core.Policy;

namespace Shadepane.Core.Tests;

public class PolicyAndStoreTests
{
    [Fact]
    public void Policy_SampleFiles_Parse()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Ext.SamplesDir, "policies"), "*.json"))
            BrowserPolicy.Parse(File.ReadAllText(file));
    }

    [Fact]
    public void Policy_UnknownField_IsRejected() =>
        // опечатка "protectedHost" не должна молча дать политику без защищённых доменов
        Assert.Throws<PolicyException>(() => BrowserPolicy.Parse("""{ "protectedHost": ["*.corp.example"] }"""));

    [Theory]
    [InlineData("""{ "protectedHosts": ["*"] }""")]
    [InlineData("""{ "protectedHosts": ["foo*.example"] }""")]
    [InlineData("""{ "protectedHosts": null }""")]
    [InlineData("""{ "blockedPermissions": [""] }""")]
    [InlineData("""{ "homeUrl": "javascript:alert(1)" }""")]
    [InlineData("""not json""")]
    public void Policy_Invalid_IsRejected(string json) => Assert.Throws<PolicyException>(() => BrowserPolicy.Parse(json));

    private static PolicySourceCandidate Source(string name, string? json, string? problem = null) =>
        new(name, () => json is not null, () => problem, () => json ?? throw new InvalidOperationException("читать нельзя"));

    [Fact]
    public void LoadFirst_TakesFirstExisting_AndFallsBackToDefault()
    {
        Assert.Equal("b", BrowserPolicy.LoadFirst([Source("a", null), Source("b", "{}"), Source("c", "{}")]).Source);
        Assert.Equal("<default>", BrowserPolicy.LoadFirst([Source("a", null)]).Source);
    }

    [Fact]
    public void LoadFirst_InsecureSource_FailsClosed_WithoutFallingThrough()
    {
        // Небезопасный реестр/файл не должен «пропускаться» к следующему источнику — иначе
        // злоумышленник выбирал бы, какую политику получит браузер.
        var ex = Assert.Throws<PolicyException>(() =>
            BrowserPolicy.LoadFirst([Source("registry", "{}", problem: "владелец S-1-5-21-x"), Source("file", "{}")]));
        Assert.Contains("registry", ex.Message);
    }

    [Fact]
    public void LoadFirst_InsecureSource_IsNotEvenRead()
    {
        var read = false;
        var candidate = new PolicySourceCandidate("x", () => true, () => "плохо", () => { read = true; return "{}"; });
        Assert.Throws<PolicyException>(() => BrowserPolicy.LoadFirst([candidate]));
        Assert.False(read);
    }

    [Fact]
    public void LoadFirst_UnreadableSource_FailsClosed_WithSourceName()
    {
        // Так выглядел файл с пустым ACL на стенде: существует, права проверены, но читать нельзя.
        var unreadable = new PolicySourceCandidate("C:\\ProgramData\\Shadepane\\policy.json",
            () => true, () => throw new UnauthorizedAccessException("Access to the path is denied."), () => "{}");
        var ex = Assert.Throws<PolicyException>(() => BrowserPolicy.LoadFirst([unreadable, Source("next", "{}")]));
        Assert.StartsWith("C:\\ProgramData", ex.Message);
        Assert.Contains("не удалось прочитать", ex.Message);
    }

    [Fact]
    public void LoadFirst_IoErrorOnRead_FailsClosed()
    {
        var locked = new PolicySourceCandidate("file", () => true, () => null, () => throw new IOException("sharing violation"));
        Assert.Contains("не удалось прочитать", Assert.Throws<PolicyException>(() => BrowserPolicy.LoadFirst([locked])).Message);
    }

    [Fact]
    public void LoadFirst_SourceNameIsNotDuplicated()
    {
        var ex = Assert.Throws<PolicyException>(() => BrowserPolicy.LoadFirst([Source("reg", "{}", problem: "владелец x")]));
        Assert.Equal(1, ex.Message.Split("reg:").Length - 1);
    }

    [Fact]
    public void LoadFirst_BrokenJson_FailsClosed_WithoutFallingThrough() =>
        Assert.Throws<PolicyException>(() => BrowserPolicy.LoadFirst([Source("a", "{"), Source("b", "{}")]));

    [Fact]
    public void Policy_AllowlistConflictsWithBlocklist_IsRejected() =>
        Assert.Throws<PolicyException>(() => BrowserPolicy.Parse("""{ "allowedPermissions": ["storage", "tabs"] }"""));

    [Theory]
    [InlineData("""{ "blockedPermissions": ["clipboardRead"] }""")]
    [InlineData("""{ "allowedPermissions": ["ClipboardRead"] }""")]
    public void Policy_ClipboardReadInPermissionLists_IsRejected(string json)
    {
        // Один источник истины — allowExtensionClipboardRead; иначе непонятно, что важнее.
        var ex = Assert.Throws<PolicyException>(() => BrowserPolicy.Parse(json));
        Assert.Contains("allowExtensionClipboardRead", ex.Message);
    }

    [Fact]
    public void Policy_ClipboardReadOption_DefaultsToUnset_AndParses()
    {
        Assert.Null(BrowserPolicy.Parse("{}").AllowExtensionClipboardRead); // не задано — решает пользователь
        Assert.False(BrowserPolicy.Parse("""{ "allowExtensionClipboardRead": false }""").AllowExtensionClipboardRead);
    }

    [Fact]
    public void Policy_AllowlistEmptyEntry_IsRejected() =>
        Assert.Throws<PolicyException>(() => BrowserPolicy.Parse("""{ "allowedPermissions": [" "] }"""));

    [Fact]
    public void Policy_SampleDefault_BlocksAtLeastBuiltInDefaults()
    {
        // Пример политики явно перечисляет права — он не должен отставать от встроенных значений.
        var sample = BrowserPolicy.Parse(File.ReadAllText(Path.Combine(Ext.SamplesDir, "policies", "default.json")));
        Assert.All(BrowserPolicy.DefaultBlockedPermissions, p => Assert.Contains(p, sample.BlockedPermissionSet));
    }

    [Fact]
    public void Store_RoundTrips_AndCorruptionIsDetected()
    {
        using var tmp = new TempDir();
        var store = new ApprovedExtensionStore(Path.Combine(tmp.Path, "approved.json"));
        Assert.Empty(store.Load());

        var rec = new ApprovedExtension("abc", "Name", "/x", "hash", "popup.html", null, DateTimeOffset.UnixEpoch);
        store.Save([rec]);
        Assert.Equal([rec], store.Load());

        File.WriteAllText(store.FilePath, "{ garbage");
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(store.FilePath, "[null]");
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void AuditLog_WritesJsonLines()
    {
        using var tmp = new TempDir();
        var log = new AuditLog(Path.Combine(tmp.Path, "sub", "audit.log"));
        log.Write("a", new { x = 1 });
        log.Write("b");
        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"Event\":\"a\"", lines[0]);
        Assert.Contains("\"x\":1", lines[0]);
    }
}
