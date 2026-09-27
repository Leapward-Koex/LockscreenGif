using System.Globalization;
using System.Runtime.InteropServices;

namespace LockscreenGif.Services.Analytics;

/// <summary>Only coarse compatibility settings belong here, never diagnostic output or machine/user identifiers.</summary>
public sealed record AnalyticsWindowsContext
{
    public uint? ProductSku { get; init; }
    public string? Release { get; init; }
    public int? Build { get; init; }
    public int? UpdateRevision { get; init; }
    public Architecture? OsArchitecture { get; init; }
    public Architecture? ProcessArchitecture { get; init; }
    public string? UserLocale { get; init; }
    public string? SystemLocale { get; init; }
    public string? DisplayLanguage { get; init; }
    public string? InstallLanguage { get; init; }
    public bool? IsElevated { get; init; }
    public bool? IsRemoteSession { get; init; }

    private static readonly Lazy<Dictionary<string, string>> KnownCultures = new(() =>
        CultureInfo
            .GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
            .Where(culture => culture.Name.Length > 0 && !culture.CultureTypes.HasFlag(CultureTypes.UserCustomCulture))
            .DistinctBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(culture => culture.Name, culture => culture.Name, StringComparer.OrdinalIgnoreCase)
    );

    internal void AddTo(Dictionary<string, object> properties)
    {
        Add("windows_product_sku", ProductSku);
        Add("windows_edition", ProductSku is { } sku ? EditionName(sku) : null);
        Add("windows_release", SafeRelease(Release));
        Add("windows_build", Build is > 0 and <= 1_000_000 ? Build : null);
        Add("windows_update_revision", UpdateRevision is >= 0 and <= 10_000_000 ? UpdateRevision : null);
        Add("os_architecture", ArchitectureName(OsArchitecture));
        Add("process_architecture", ArchitectureName(ProcessArchitecture));
        Add("windows_user_locale", SafeCulture(UserLocale));
        Add("windows_system_locale", SafeCulture(SystemLocale));
        Add("windows_display_language", SafeCulture(DisplayLanguage));
        Add("windows_install_language", SafeCulture(InstallLanguage));
        Add("is_elevated", IsElevated);
        Add("is_remote_session", IsRemoteSession);

        void Add(string name, object? value)
        {
            if (value is not null)
            {
                properties.Add(name, value);
            }
        }
    }

    private static string? SafeCulture(string? value) =>
        value is { Length: > 0 and <= 85 } && KnownCultures.Value.TryGetValue(value, out var canonical) ? canonical : null;

    private static string? SafeRelease(string? value) =>
        value is { Length: 4 }
        && char.IsAsciiDigit(value[0])
        && char.IsAsciiDigit(value[1])
        && ((value[2] == 'H' && value[3] is '1' or '2') || (char.IsAsciiDigit(value[2]) && char.IsAsciiDigit(value[3])))
            ? value
            : null;

    private static string? ArchitectureName(Architecture? value) =>
        value switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

    // GetProductInfo PRODUCT_* constants. Preserve the numeric SKU for editions not yet mapped here.
    // https://learn.microsoft.com/windows/win32/api/sysinfoapi/nf-sysinfoapi-getproductinfo
    private static string EditionName(uint sku) =>
        sku switch
        {
            0x65 => "home",
            0x62 => "home_n",
            0x63 => "home_china",
            0x64 => "home_single_language",
            0x30 => "pro",
            0x31 => "pro_n",
            0xA1 => "pro_workstation",
            0xA2 => "pro_workstation_n",
            0xA4 => "pro_education",
            0x79 => "education",
            0x7A => "education_n",
            0x04 => "enterprise",
            0x1B => "enterprise_n",
            0x48 => "enterprise_evaluation",
            0x54 => "enterprise_n_evaluation",
            0x7D => "enterprise_ltsc",
            0x7E => "enterprise_ltsc_n",
            0x81 => "enterprise_ltsc_evaluation",
            0x82 => "enterprise_ltsc_n_evaluation",
            0xAF => "enterprise_multi_session",
            0xBC => "iot_enterprise",
            0xBF => "iot_enterprise_ltsc",
            0x07 => "server_standard",
            0x08 => "server_datacenter",
            0x4F => "server_standard_evaluation",
            0x50 => "server_datacenter_evaluation",
            _ => "unknown",
        };
}
