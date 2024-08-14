using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LightBuzz.Archiver;
using Newtonsoft.Json;

public struct StoryRemoteHeader {
    public string story;
    public string[] chapters;
}

public class ChapterResources {
    
    public ResourceTree backgrounds = new();
    public ResourceTree items = new();
    public ResourceTree characters = new();
    public ResourceTree sounds = new();

    public ChapterResources CompareTo(ChapterResources external) {
        
        external.backgrounds = Compare(backgrounds, external.backgrounds);
        external.items = Compare(items, external.items);
        external.characters = Compare(characters, external.characters);
        external.sounds = Compare(sounds, external.sounds);

        backgrounds.Id.UnionWith(external.backgrounds.Id);
        items.Id.UnionWith(external.items.Id);
        characters.Id.UnionWith(external.characters.Id);
        sounds.Id.UnionWith(external.sounds.Id);
        
        return external;
    }
    
    private ResourceTree Compare(ResourceTree a, ResourceTree b) {
        var ids = b.Id.ToArray();
        
        b.Id.Clear();
        
        ids.Where(i => !a.Id.Contains(i)).Each(e => b.Id.Add(e));
        
        return b;
    }
}

public class ResourceTree {
    public HashSet<string> Id { get; } = new();
    public void Add(string id) => Id.Add(id);
    public void Add(string[] id) => Id.UnionWith(id);
}

public class ProjectManagement {
    private const int BUFFER_SIZE = 1024 * 1024;
    
    private const string FLOW_JSON = "flow.json";
    private const string STORY_JSON = "index.json";
    private const string STORY_COVER = "story_cover.jpg";
    private const string CHAPTER_JSON = "chapter.json";
    private const string EXPORT_JSON = "content.elp";
    private const string BLACKBOARD_JSON = "blackboard.json";
    
    public async Task ExportResources(RemoteContent story) {
        var cache = new ChapterResources();
        
        await ExportChapters(story, cache);

        await ExportStory(story);
    }

    public async Task<string> ImportStory(string headerPath, string root) {

        var headerSource = await File.ReadAllTextAsync(headerPath);
        var header = headerSource.Deserialize<StoryRemoteHeader>();
        var importLocation = Path.GetDirectoryName(headerPath);

        return await DecompressStory(importLocation, header.story, root);
    }

    private async Task<string> DecompressStory(string location, string id, string root) {
        var destination = Path.Combine(root, id);
        
        await Task.Run(() => {
            var path = Path.Combine(location, id + ".zip");
            Directory.CreateDirectory(destination);
            Archiver.Decompress(path, destination, true);
        });

        return destination;
    }

    private async Task<string> DecompressChapter(string zip, string destination) {
        
        await Task.Run(() => {
            Directory.CreateDirectory(destination);
            Archiver.Decompress(zip, destination, true);
        });

        return destination;
    }
    
    public async Task RestoreChapter(string zip, string target) => await DecompressChapter(zip, target);

    private async Task ExportChapters(RemoteContent story, ChapterResources cache) {
        var chapters = story.chapters.Select(c => ExportChapter(cache, c, story.path)).ToList();

        await Task.WhenAll(chapters);
    }
    
    private async Task ExportChapter(ChapterResources global, StoryChapter chapter, string storyPath) {
        var flowPath = Path.Combine(chapter.path, FLOW_JSON);

        if (!File.Exists(flowPath)) return;
        
        var boardPath = Path.Combine(storyPath, BLACKBOARD_JSON);
        
        var source = await File.ReadAllTextAsync(flowPath);
        var flow = source.Deserialize<FlowData>();

        source = await File.ReadAllTextAsync(boardPath);
        var board = source.Deserialize<BlackboardData>();
        
        var tree = global.CompareTo(GetTree(flow));
        var exportPath = Path.Combine(storyPath, "Export", chapter.id);
        
        var files = board
            .GetResources(tree)
            .Select(r => CopyResource(Path.Combine(storyPath, "blackboard"), exportPath, r.Value))
            .ToList();
        
        //files.Add(CopyResource(chapter.path,exportPath, new[] {CHAPTER_JSON, FLOW_JSON}));
        
        await Task.WhenAll(files);

        await ArchivePath(exportPath, chapter.id);

        CleanUp(exportPath);
    }

    private async Task ExportStory(RemoteContent story) {
        var targetPath = Path.Combine(story.path, "Export", story.id);

        Directory.CreateDirectory(targetPath);
        
        await CopyResource(story.path, targetPath, new[] { BLACKBOARD_JSON, STORY_JSON, STORY_COVER });

        CopyPath(story.path, targetPath, story.chapters.Where(c => Directory.Exists(Path.Combine(story.path, c.id))).Select(c => c.id).ToArray());

        await ArchivePath(targetPath, story.id);
        
        CleanUp(targetPath);
        
        var header = new {
            story = story.id,
            chapters = story.chapters.Select(c => c.id).ToArray(),
        };

        await File.WriteAllTextAsync(Path.Combine(story.path, "Export", EXPORT_JSON), header.Serialize());
    }

    private void CleanUp(string path) {
        if(Directory.Exists(path))
            Directory.Delete(path, true);
    }
    
    private async Task ArchivePath(string path, string id) {
        var directoryInfo = Directory.GetParent(path);
        if (directoryInfo != null) {
            var zipPath = Path.Combine(directoryInfo.FullName, id) + ".zip";
            await Task.Run(() => Archiver.Compress(path, zipPath, true));
        }
    }

    private void CopyPath(string root, string target, string[] paths) {
        paths.Each(Copy);

        void Copy(string path) {
            var sourcePath = Path.Combine(root, path);
            var targetPath = Path.Combine(target, path);
            
            var dir = new DirectoryInfo(sourcePath);

            if (!dir.Exists) return;

            var dirs = dir.GetDirectories();
            Directory.CreateDirectory(targetPath);

            foreach (var file in dir.GetFiles()) {
                var targetFilePath = Path.Combine(targetPath, file.Name);
                file.CopyTo(targetFilePath, true);
            }

            foreach (var subDir in dirs) {
                var newDestinationDir = Path.Combine(targetPath, subDir.Name);
                Copy(Path.Combine(subDir.FullName, newDestinationDir));
            }
        }
    }
    
    private async Task CopyResource(string root, string targetFolder, string[] sources) {

        var task = sources.Select(Copy).ToList();

        await Task.WhenAll(task);
        
        async Task Copy(string source) {
            var sourcePath = Path.Combine(root, source);
            var targetPath = Path.Combine(targetFolder, source);
            var checkPath = Path.GetDirectoryName(targetPath);
        
            Directory.CreateDirectory(checkPath);

            using var fileStream = new FileStream(targetPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
        
            fileStream.SetLength(fs.Length);
            var bytesRead = -1;
            var bytes = new byte[BUFFER_SIZE];

            while ((bytesRead = await fs.ReadAsync(bytes, 0, BUFFER_SIZE)) > 0)
                await fileStream.WriteAsync(bytes, 0, bytesRead);    
        }
        
    }

    private ChapterResources GetTree(FlowData flow) {
        var tree = new ChapterResources(); 
        var blocks = new List<object>();
        
        flow.commands.Each(block => blocks.AddRange(block.commands));

        var resolvers = blocks.Select(DeserializeCommand).Where(r => r != null).ToArray();
        
        resolvers.Each(r => r.Resolve(tree));
        
        return tree;
    }

    private ICommandResolver DeserializeCommand(object data) {
        var header = data.Deserialize<CommandHeader>();
        var commandType = Type.GetType(header.Type.Replace("Command", "Resolver"));
        if (commandType == null) return null;
        
        var resolver = Convert.ChangeType(JsonConvert.DeserializeObject(data.ToString(), commandType), commandType);
        
        return (ICommandResolver)resolver;
    }
    
}