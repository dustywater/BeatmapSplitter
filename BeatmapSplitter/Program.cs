using System.Text.Json;
using Coosu.Beatmap;
using Coosu.Beatmap.Sections.HitObject;
using System.IO.Compression;
using System.Net;

var tosuAddress = "127.0.0.1";
var tosuPort = 24050;

if (args.Length == 0 || !int.TryParse(args[0], out var numParts))
{
    Console.WriteLine("Invalid number of parts. Usage: BeatmapSplitter <number of parts> [tosu address] [tosu port]");
    Console.WriteLine("Address and port will default to 127.0.0.1:24050 (tosu defaults) if not specified.");
    return;
}

if (args.Length > 1)
    if (!IPAddress.TryParse(args[1], out _))
    {
        Console.WriteLine("Invalid tosu address. Usage: BeatmapSplitter <number of parts> [tosu address] [tosu port]");
        return;
    }

if (args.Length > 2)
{
    if (!int.TryParse(args[2], out tosuPort) || (tosuPort < 1 || tosuPort > 65535))
    {
        Console.WriteLine("Invalid tosu port. Usage: BeatmapSplitter <number of parts> [tosu address] [tosu port]");
        return;
    }
}

var mapPath = await GetCurrentBeatmapPath();
if (mapPath == null) return;

if (await SplitMap(mapPath, numParts))
{ 
    ReimportMap(mapPath);   
}


async Task<bool> SplitMap(string path, int parts)
{
    var mapFile = await OsuFile.ReadFromFileAsync(path);
    
    if (mapFile.HitObjects?.HitObjectList == null) return false;
    if (mapFile.Metadata == null) return false;

    var splitObjectList = mapFile.HitObjects.HitObjectList
        .Select((item, index) => new { item, index })
        .GroupBy(x => x.index * parts / mapFile.HitObjects.HitObjectList.Count)
        .Select(g => g.Select(x => x.item).ToList())
        .ToList();

    if (splitObjectList.Count < parts) return false;
    
    for (var i = 1; i < splitObjectList.Count; i++)
    {
        // Not the first section. Make sure this section starts with a new combo by moving objects from the end of the previous section.
        // Give up and leave it alone if the number of objects needed to be shifted exceeds 10% of the section.
        var numShifted = 0;
        var tempSection = splitObjectList[i];
        while ((tempSection.First().RawType & RawObjectType.NewCombo) != RawObjectType.NewCombo &&
               numShifted < splitObjectList[i].Count / 10)
        {
            tempSection.Insert(0, splitObjectList[i - 1].Last());
            splitObjectList[i - 1].RemoveAt(splitObjectList[i - 1].Count - 1);
            numShifted++;
        }

        if ((tempSection.First().RawType & RawObjectType.NewCombo) == RawObjectType.NewCombo)
        {
            splitObjectList[i] = tempSection;
        }
    }

    var originalVersion = mapFile.Metadata?.Version;
    
    foreach (var section in splitObjectList)
    {
        // Create maps for each section.
        mapFile.HitObjects.HitObjectList = section;
        if (mapFile.Metadata != null)
        {
            mapFile.Metadata.Version =
                originalVersion + $" [{splitObjectList.IndexOf(section) + 1}/{splitObjectList.Count}]";

            string? directory = Path.GetDirectoryName(mapFile.OriginalPath);
            if (directory == null)
            {
                Console.WriteLine("Could not determine map directory");
                return false;
            }

            mapFile.SaveToDirectory(directory);
            Console.WriteLine($"Created: {mapFile.GetOsuFilename(mapFile.Metadata.Version)}");
        }
    }
    return true;
}

void ReimportMap(string path)
{
    var mapDirectory = Path.GetDirectoryName(path);
    if (mapDirectory == null)
    {
        Console.WriteLine("Failed to reimport map");
        return;
    }

    var parentDirectory = Directory.GetParent(mapDirectory);
    if (parentDirectory == null)
    {
        Console.WriteLine("Failed to reimport map");
        return;
    }

    var zipPath = Path.Combine(parentDirectory.FullName, Path.GetFileName(mapDirectory) + ".osz");
    
    if (File.Exists(zipPath))
        File.Delete(zipPath);
        
    ZipFile.CreateFromDirectory(mapDirectory, zipPath);
}

// Get the path of the current beatmap from tosu.
async Task<string?> GetCurrentBeatmapPath()
{
    using var client = new HttpClient();
    try
    {
        var response = await client.GetStringAsync($"http://{tosuAddress}:{tosuPort}/json/v2");
        var data = JsonSerializer.Deserialize<TosuResponse>(response);
        
        if (data?.folders.songs == null)
        {
            Console.WriteLine("Error getting beatmap path (Is tosu running?)");
            return null;
        }

        var adjustedSongs = data.folders.songs.Replace("\\", Path.DirectorySeparatorChar.ToString());
        var adjustedMap = data.directPath.beatmapFile.Replace("\\", Path.DirectorySeparatorChar.ToString());
        
        if (Path.DirectorySeparatorChar == '/')
        {
            // Not Windows so remove the first 2 characters (Example: C:)
            adjustedSongs = adjustedSongs.Remove(0,2);
        }
        
        return Path.Combine(adjustedSongs, adjustedMap);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error getting beatmap path (Is tosu running?): {ex.Message}");
        return null;
    }
}


public class TosuResponse
{
    public required Folders folders { get; set; }
    public required DirectPath directPath { get; set; }
}

public class Folders
{
    public required string songs { get; set; }
}

public class DirectPath
{
    public required string beatmapFile { get; set; }
}
