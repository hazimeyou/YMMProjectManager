using System.IO;

namespace YMMProjectManager.Application;

public sealed class ProjectListConflictException : IOException
{
    public ProjectListConflictException() : base("Project list changed after it was loaded.") { }
}

public sealed class ProjectListBusyException : IOException
{
    public ProjectListBusyException(IOException inner) : base("Project list is being saved by another process.", inner) { }
}
