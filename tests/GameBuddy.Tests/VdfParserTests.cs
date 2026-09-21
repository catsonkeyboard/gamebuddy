using GameBuddy.Services.Scanning;

namespace GameBuddy.Tests;

/// <summary>
/// VDF 解析是整个 Steam 扫描的地基：这里锁住"根对象名(AppState/libraryfolders)必须被保留"这个
/// 曾经踩过的坑——早期实现直接跳到第一个 '{'，导致根键丢失、扫描恒返回 0。
/// </summary>
public class VdfParserTests
{
    private const string AppManifest = """
        "AppState"
        {
        	"appid"		"570"
        	"Universe"		"1"
        	"LauncherPath"		"C:\\Program Files (x86)\\Steam\\steam.exe"
        	"name"		"Dota 2"
        	"StateFlags"		"4"
        	"installdir"		"dota 2 beta"
        	"LastUpdated"		"1700000000"
        	"SizeOnDisk"		"32212254720"
        	"UpdateResult"		"0"
        	"BytesToDownload"		"0"
        }
        """;

    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"totalsize"		"0"
        		"apps"
        		{
        			"570"		"32212254720"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"D 盘"
        		"apps"
        		{
        			"1174180"		"1000"
        		}
        	}
        }
        """;

    [Fact]
    public void Parse_AppManifest_KeepsRootObjectName()
    {
        var root = VdfParser.Parse(AppManifest);

        var state = VdfParser.GetObject(root, "AppState");
        Assert.NotNull(state);
        Assert.Equal("570", VdfParser.GetString(state!, "appid"));
        Assert.Equal("Dota 2", VdfParser.GetString(state!, "name"));
        Assert.Equal("dota 2 beta", VdfParser.GetString(state!, "installdir"));
        Assert.Equal("32212254720", VdfParser.GetString(state!, "SizeOnDisk"));
    }

    [Fact]
    public void Parse_LibraryFolders_ReturnsEveryLibraryPath()
    {
        var root = VdfParser.Parse(LibraryFolders);

        var folders = VdfParser.GetObject(root, "libraryfolders");
        Assert.NotNull(folders);

        var paths = folders!.Values
            .OfType<Dictionary<string, object>>()
            .Select(entry => VdfParser.GetString(entry, "path"))
            .Where(p => p is not null)
            .ToList();

        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\Program Files (x86)\Steam", paths);
        Assert.Contains(@"D:\SteamLibrary", paths);
    }

    [Fact]
    public void Parse_IsCaseInsensitive_OnKeys()
    {
        var root = VdfParser.Parse("\"AppState\"\n{\n\t\"AppID\"\t\"570\"\n}");
        var state = VdfParser.GetObject(root, "appstate");

        Assert.NotNull(state);
        Assert.Equal("570", VdfParser.GetString(state!, "APPID"));
    }

    [Fact]
    public void Parse_IgnoresCommentsAndHandlesNestedObjects()
    {
        var text = """
            // 这是注释
            "Root"
            {
            	"outer"
            	{
            		"inner"		"value"
            	}
            	"flag"		"1"
            }
            """;

        var root = VdfParser.Parse(text);
        var obj = VdfParser.GetObject(root, "Root");

        Assert.NotNull(obj);
        Assert.Equal("1", VdfParser.GetString(obj!, "flag"));

        var nested = VdfParser.GetObject(obj!, "outer");
        Assert.NotNull(nested);
        Assert.Equal("value", VdfParser.GetString(nested!, "inner"));
    }
}
