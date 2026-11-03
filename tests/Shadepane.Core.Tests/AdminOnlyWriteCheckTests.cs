using Shadepane.Core.Policy;

namespace Shadepane.Core.Tests;

public class AdminOnlyWriteCheckTests
{
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const int ReadAndExecute = 0x1200A9;
    private const int FullControl = 0x1F01FF;
    private const int GenericWrite = 0x40000000;

    private static AceInfo Allow(string sid, int mask, bool inheritOnly = false) => new(sid, true, inheritOnly, mask);

    private static readonly AceInfo[] AdminOnly =
    [
        Allow(AdminOnlyWriteCheck.Administrators, FullControl),
        Allow(AdminOnlyWriteCheck.LocalSystem, FullControl),
        Allow(Users, ReadAndExecute),
    ];

    [Fact]
    public void AdminOwnedAndAdminWritable_IsSecure() =>
        Assert.Null(AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.Administrators, AdminOnly, AdminOnlyWriteCheck.FileWriteMask));

    [Fact]
    public void UserOwned_IsInsecure_EvenWithAdminOnlyAcl() =>
        // владелец может в любой момент переписать ACL
        Assert.Contains(SomeUser, AdminOnlyWriteCheck.FindProblem(SomeUser, AdminOnly, AdminOnlyWriteCheck.FileWriteMask));

    [Fact]
    public void UnknownOwner_IsInsecure() =>
        Assert.NotNull(AdminOnlyWriteCheck.FindProblem(null, AdminOnly, AdminOnlyWriteCheck.FileWriteMask));

    [Theory]
    [InlineData(Users, 0x2)]                 // WriteData / CreateFiles
    [InlineData(Users, 0x4)]                 // AppendData / CreateDirectories (как в ProgramData по умолчанию)
    [InlineData(AuthenticatedUsers, 0x10000)] // Delete — можно удалить и подложить свой файл
    [InlineData(SomeUser, 0x40000)]          // WRITE_DAC
    [InlineData(SomeUser, GenericWrite)]
    [InlineData(SomeUser, FullControl)]
    public void NonAdminWriter_IsInsecure(string sid, int mask) =>
        Assert.Contains(sid, AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.Administrators, [.. AdminOnly, Allow(sid, mask)], AdminOnlyWriteCheck.FileWriteMask));

    [Fact]
    public void DeleteChildOnDirectory_IsInsecure_ButNotCheckedForFiles()
    {
        var aces = new[] { Allow(Users, 0x40) };
        Assert.NotNull(AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.Administrators, aces, AdminOnlyWriteCheck.DirectoryWriteMask));
        Assert.Null(AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.Administrators, aces, AdminOnlyWriteCheck.FileWriteMask));
    }

    [Fact]
    public void InheritOnlyDenyAndCreatorOwner_AreIgnored()
    {
        AceInfo[] aces =
        [
            Allow(Users, FullControl, inheritOnly: true),               // действует только на дочерние объекты
            new(Users, IsAllow: false, IsInheritOnly: false, FullControl), // deny
            Allow(AdminOnlyWriteCheck.CreatorOwner, FullControl),
        ];
        Assert.Null(AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.TrustedInstaller, aces, AdminOnlyWriteCheck.FileWriteMask));
    }

    [Fact]
    public void RegistrySetValue_ByUsers_IsInsecure() =>
        Assert.NotNull(AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.Administrators, [Allow(Users, 0x2)], AdminOnlyWriteCheck.RegistryWriteMask));

    [Fact]
    public void RegistryReadKey_ByUsers_IsSecure() =>
        Assert.Null(AdminOnlyWriteCheck.FindProblem(AdminOnlyWriteCheck.Administrators, [Allow(Users, 0x20019)], AdminOnlyWriteCheck.RegistryWriteMask));
}
