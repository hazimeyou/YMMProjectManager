using System.IO;

namespace YMMProjectManager.Application;

//CSJ,class=ProjectListConflictException,プロジェクト一覧競合例外
public sealed class ProjectListConflictException : IOException
{
    public ProjectListConflictException() : base("Project list changed after it was loaded.") { }
}

//CSJ,class=ProjectListBusyException,プロジェクト一覧の処理中例外
public sealed class ProjectListBusyException : IOException
{
    public ProjectListBusyException(IOException inner) : base("Project list is being saved by another process.", inner) { }
}
