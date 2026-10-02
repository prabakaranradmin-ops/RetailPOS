using Pos.Core.Data;
using Pos.Core.Domain;
using Pos.Core.Domain.Import;
using Pos.TestSupport;
using Xunit;

namespace Pos.Core.Tests;

/// <summary>
/// Finding an item by what the customer calls it: its Tamil name, typed in Tamil or spelled in
/// English, and an English name spelled the way it is said.
/// </summary>
public class TamilSearchTests : IDisposable
{
    private readonly TempDatabase _temp = new();

    public void Dispose() => _temp.Dispose();

    private ItemRepository Items => _temp.Items;

    private static Item Dal(string? nameTa = "துவரம் பருப்பு", string name = "Toor Dal 1kg") =>
        Catalogue.Item(sku: "DAL001", barcode: "8901234567890", name: name, price: 189m) with { NameTa = nameTa };

    private IEnumerable<string> Found(string query) => Items.Search(query).Select(i => i.Sku);

    [Fact]
    public void TheTamilNameIsKeptAndReadBack()
    {
        Items.AddRange([Dal()]);

        Assert.Equal("துவரம் பருப்பு", Items.FindBySku("DAL001")!.NameTa);
        Assert.Equal("துவரம் பருப்பு", Items.FindByBarcode("8901234567890")!.NameTa);
    }

    [Fact]
    public void AnItemWithNoTamilNameHasNone()
    {
        Items.AddRange([Dal(nameTa: "   ")]);

        Assert.Null(Items.FindBySku("DAL001")!.NameTa);
    }

    [Theory]
    [InlineData("பருப்பு")]
    [InlineData("துவரம்")]
    [InlineData("paruppu")]
    [InlineData("baruppu")]
    [InlineData("thuvaram")]
    [InlineData("Thuvaram Paruppu")]
    public void TheTamilNameFindsItTypedInTamilOrSpelledInEnglish(string query)
    {
        Items.AddRange([Dal(), Catalogue.Item(sku: "SUG001", name: "Sugar Loose", price: 45m) with { NameTa = "சர்க்கரை" }]);

        Assert.Equal(["DAL001"], Found(query));
    }

    [Theory]
    [InlineData("kadalai mavu")]
    [InlineData("gadalai maavu")]
    [InlineData("கடலை மாவு")]
    public void AnEnglishNameIsFoundSpelledTheWayItIsSaid(string query)
    {
        Items.AddRange([Catalogue.Item(sku: "BESAN", name: "Kadalai Maavu 500g", price: 60m)]);

        Assert.Equal(["BESAN"], Found(query));
    }

    [Fact]
    public void ANameMatchedAsTypedComesBeforeOneThatOnlySoundsLikeIt()
    {
        Items.AddRange(
        [
            Dal(),
            Catalogue.Item(sku: "VADAI", name: "Paruppu Vadai Mix", price: 85m),
        ]);

        Assert.Equal(["VADAI", "DAL001"], Found("paruppu"));
    }

    [Fact]
    public void TooLittleTypedToSoundLikeAnythingFindsNothingByIt()
    {
        Items.AddRange([Dal()]);

        // ப folds to "pa": two letters, which would match half the catalogue.
        Assert.Empty(Found("ப"));
    }

    [Fact]
    public void AnInactiveItemIsNotFoundByHowItSounds()
    {
        Items.AddRange([Dal() with { IsActive = false }]);

        Assert.Empty(Found("paruppu"));
    }

    [Fact]
    public void AReImportWithNoTamilNameKeepsTheOneTheItemHas()
    {
        Items.AddRange([Dal()]);

        // A price revision from a file with no name_ta column, and a new English name.
        Items.UpsertRange([Dal(nameTa: null, name: "Tur Dal Premium 1kg")]);

        var dal = Items.FindBySku("DAL001")!;
        Assert.Equal("துவரம் பருப்பு", dal.NameTa);
        Assert.Equal(["DAL001"], Found("paruppu"));
        Assert.Equal(["DAL001"], Found("premium"));
    }

    [Fact]
    public void AReImportWithATamilNameReplacesIt()
    {
        Items.AddRange([Dal()]);

        Items.UpsertRange([Dal(nameTa: "துவரை")]);

        Assert.Equal("துவரை", Items.FindBySku("DAL001")!.NameTa);
        Assert.Empty(Found("paruppu"));
        Assert.Equal(["DAL001"], Found("thuvarai"));
    }

    [Fact]
    public void ItemsFromBeforeTheKeysGetThemWhenTheTillStarts()
    {
        Items.AddRange([Dal(), Catalogue.Item(sku: "BESAN", name: "Kadalai Maavu 500g", price: 60m)]);

        // As an upgraded database has them: names, and no keys yet.
        using (var connection = _temp.Database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE items SET sound_name = NULL, sound_ta = NULL;";
            command.ExecuteNonQuery();
        }

        Assert.Empty(Found("paruppu"));

        _temp.Database.EnsureMigrated();

        Assert.Equal(["DAL001"], Found("paruppu"));
        Assert.Equal(["BESAN"], Found("gadalai"));

        // And once filled, starting again has nothing to do.
        using var again = _temp.Database.OpenConnection();
        Assert.Equal(0, ItemRepository.FillSoundKeys(again));
    }

    // ---- From a file ----------------------------------------------------------------------------

    private const string Header = "sku,barcode,name,hsn_code,unit,mrp,selling_price,gst_rate,is_weighed";

    [Fact]
    public void AFileCarriesTheTamilName()
    {
        var (items, problems) = ItemCsvParser.Parse(new StringReader(
            Header + ",name_ta\nDAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189,189,5,no,துவரம் பருப்பு\nSUG001,,Sugar,1701,Kg,45,45,5,yes,\n"));

        Assert.Empty(problems);
        Assert.Equal("துவரம் பருப்பு", items[0].Item.NameTa);
        Assert.Null(items[1].Item.NameTa);
    }

    [Fact]
    public void AFileWithoutTheColumnLoadsAsBefore()
    {
        var (items, problems) = ItemCsvParser.Parse(new StringReader(Header + "\nDAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189,189,5,no\n"));

        Assert.Empty(problems);
        Assert.Null(Assert.Single(items).Item.NameTa);
    }

    [Fact]
    public void ATamilNameTooLongIsRefused()
    {
        var name = new string('அ', ItemCsvParser.MaxNameTaLength + 1);

        var (items, problems) = ItemCsvParser.Parse(new StringReader(Header + $",name_ta\nDAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189,189,5,no,{name}\n"));

        Assert.Empty(items);
        var problem = Assert.Single(problems);
        Assert.Equal("name_ta", problem.Column);
        Assert.Equal($"the Tamil name is 121 characters long; 120 is the most.", problem.Problem);
    }

    [Fact]
    public void AnImportedTamilNameIsFoundAtOnce()
    {
        var result = new ItemImporter(Items).Import(new StringReader(
            Header + ",name_ta\nDAL001,8901234567890,Toor Dal 1kg,0713,Pcs,189,189,5,no,துவரம் பருப்பு\n"), updateExisting: false, dryRun: false);

        Assert.True(result.Committed);
        Assert.Equal(["DAL001"], Found("paruppu"));
    }
}
