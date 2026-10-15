using System.Text;
using Vidsc.Core.Projects;
using Vidsc.Media.Tooling;

namespace Vidsc.App.Services;

public static class EditorProjectStore
{
    public static void Save(string path, EditorProject project)
    {
        string destination = Path.GetFullPath(path);
        if (FfmpegCli.PathsResolveToSameFile(destination, project.Source.Path)
            || (!string.IsNullOrWhiteSpace(project.Settings.Destination) && FfmpegCli.PathsResolveToSameFile(destination, project.Settings.Destination)))
            throw new IOException("Project path must differ from source and export destination.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(ProjectJson.Serialize(project));
                stream.Write(bytes); stream.Flush(true);
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static EditorProject Load(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Editor project exceeds the size limit.");
        return ProjectJson.Deserialize(File.ReadAllText(path));
    }
}
