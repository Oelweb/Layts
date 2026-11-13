using System.Text.Json.Nodes;

namespace Layts.Games.Swyf;

/// <summary>An app/scam from the in-game KolkataOS store.</summary>
public sealed class SwyfProduct
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public long Price { get; init; }

    /// <summary>"run" = belongs to the current run, "permanent" = kept across runs.</summary>
    public string Ownership { get; init; } = "run";
    public string Category { get; init; } = "";
    public int UnlockDay { get; init; }

    public bool IsPermanent => Ownership == "permanent";
    public bool IsScam => Category == "scam";
}

/// <summary>
/// Reads the store catalog straight from the game install
/// (StreamingAssets\kolkata-os\application-store.json), so new apps from game updates show up automatically.
/// Falls back to a built-in list when the game files can't be found.
/// </summary>
public static class SwyfCatalog
{
    public static bool LoadedFromGame { get; private set; }

    public static List<SwyfProduct> Load()
    {
        try
        {
            var install = SwyfLocator.FindInstallDirectory();
            if (install != null)
            {
                var file = Path.Combine(install, "Scam With Your Friends_Data", "StreamingAssets", "kolkata-os", "application-store.json");
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["products"] is JsonArray products)
                {
                    var list = new List<SwyfProduct>();
                    foreach (var p in products.OfType<JsonObject>())
                    {
                        // Only things you actually buy – included apps and day unlocks need no editing.
                        if ((string?)p["accessMode"] != "purchase") continue;
                        var id = (string?)p["id"];
                        if (string.IsNullOrEmpty(id)) continue;
                        list.Add(new SwyfProduct
                        {
                            Id = id,
                            Title = (string?)p["title"] ?? id,
                            Description = (string?)p["description"] ?? "",
                            Price = SwyfJson.Long(p["price"]) ?? 0,
                            Ownership = (string?)p["ownership"] ?? "run",
                            Category = (string?)p["category"] ?? "",
                            UnlockDay = (int)(SwyfJson.Long(p["unlockDay"]) ?? 0),
                        });
                    }
                    if (list.Count > 0)
                    {
                        LoadedFromGame = true;
                        return Sort(list);
                    }
                }
            }
        }
        catch
        {
            // fall through to the built-in list
        }

        LoadedFromGame = false;
        return Sort(Fallback.Select(f => new SwyfProduct
        {
            Id = f.Id, Title = f.Title, Price = f.Price, Ownership = f.Own, Category = f.Cat, UnlockDay = f.Day,
        }).ToList());
    }

    private static List<SwyfProduct> Sort(List<SwyfProduct> list) =>
        list.OrderBy(p => p.IsScam ? 0 : 1).ThenBy(p => p.UnlockDay).ThenBy(p => p.Title).ToList();

    // Snapshot of the store (game version 0.1.81).
    private static readonly (string Id, string Title, long Price, string Own, string Cat, int Day)[] Fallback =
    {
        ("tic-tac-toe-license", "Tic-Tac-Toe", 25, "permanent", "cosmetic", 14),
        ("scam-photo-blackmail", "Blackmail Scam", 300, "run", "scam", 11),
        ("scam-ransom", "Ransom", 0, "run", "scam", 9),
        ("store-games-access", "Games & Software Tab", 300, "run", "storefront", 3),
        ("store-physical-access", "Items Shop Tab", 300, "permanent", "storefront", 6),
        ("dev-scam-romance", "Romance Scam", 0, "run", "scam", 2),
        ("dev-scam-police-impersonation", "Police Impersonation", 0, "run", "scam", 5),
        ("dev-scam-tech-support", "Tech Support Scam", 0, "run", "scam", 4),
        ("dev-scam-investment", "Investment Scam", 0, "run", "scam", 5),
        ("malwarebits-pro", "Malwarebits Pro", 500, "run", "functional", 6),
        ("malwarebits-enterprise", "Malwarebits Enterprise", 1250, "run", "functional", 11),
        ("jw-paint-license", "JW Paint", 3000, "permanent", "cosmetic", 13),
        ("meteor-cookie-license", "Meteor Cookie", 2250, "permanent", "cosmetic", 7),
        ("scam-social-security", "Social Security", 0, "run", "scam", 3),
        ("scam-car-registration", "Car's Extended Warranty", 0, "run", "scam", 6),
        ("scam-gift-card", "Gift Card", 150, "run", "scam", 1),
        ("scam-credit-card", "Credit Card", 0, "run", "scam", 1),
        ("scam-password-recovery", "Password Recovery", 600, "run", "scam", 5),
        ("scam-remote-desktop", "AnyViewer", 0, "run", "scam", 7),
        ("scam-bitcoin-drain", "Bitcoin", 600, "run", "scam", 4),
        ("scam-meme-coin", "Meme Coin Rug Pull", 800, "run", "scam", 8),
        ("ledger-business-license", "Ledger", 100, "permanent", "functional", 9),
        ("slots-business-license", "Rainbit", 2000, "permanent", "functional", 10),
        ("messenger-business-license", "Disscord", 150, "permanent", "functional", 4),
        ("zoomy-license", "Zoomy", 250, "permanent", "functional", 8),
        ("backgrounds-included", "Backgrounds", 50, "permanent", "functional", 2),
        ("scam-lottery-prize", "Lottery Prize", 0, "run", "scam", 10),
        ("scam-nigerian-prince", "Nigerian Prince", 0, "run", "scam", 6),
        ("scam-book-publishing", "Book Publishing", 0, "run", "scam", 13),
        ("scam-business-investment", "Business Investment", 0, "run", "scam", 15),
        ("scam-timeshare", "Timeshare", 0, "run", "scam", 12),
        ("scam-ip-geolocation", "IP Geolocation", 0, "run", "scam", 6),
        ("scam-money-mule", "Money Mule", 0, "run", "scam", 11),
    };
}
