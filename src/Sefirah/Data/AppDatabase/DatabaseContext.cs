using Sefirah.Data.AppDatabase.Models;
using SQLite;
namespace Sefirah.Data.AppDatabase;

public class DatabaseContext : IDisposable
{
    public SQLiteConnection Database { get; private set; }
    private bool _disposed = false;

    public DatabaseContext(ILogger<DatabaseContext> logger)
    {
        try
        {
            logger.LogInformation("正在初始化数据库上下文");
            Database = TryCreateDatabase();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "初始化数据库上下文失败：{ex}");
            throw;
        }
    }

    private static SQLiteConnection TryCreateDatabase()
    {
        var databasePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "sefirah.db");
        var db = new SQLiteConnection(databasePath);

        if (db.GetTableInfo(nameof(LocalDeviceEntity)).Count == 0)
        {
            db.CreateTable<LocalDeviceEntity>();
        }

        if (db.GetTableInfo(nameof(RemoteDeviceEntity)).Count == 0)
        {
            db.CreateTable<RemoteDeviceEntity>();
        }
        else
        {
            // Check if Model column exists, if not add it (migration for existing databases)
            var remoteDeviceColumns = db.GetTableInfo(nameof(RemoteDeviceEntity));
            var hasModelColumn = remoteDeviceColumns.Any(col => col.Name.Equals("Model", StringComparison.OrdinalIgnoreCase));
            var hasPublicKeyColumn = remoteDeviceColumns.Any(col => col.Name.Equals("PublicKey", StringComparison.OrdinalIgnoreCase));
            var hasSentSftpRequestColumn = remoteDeviceColumns.Any(col => col.Name.Equals("HasSentSftpRequest", StringComparison.OrdinalIgnoreCase));
            
            if (!hasModelColumn)
            {
                try
                {
                    db.Execute("ALTER TABLE RemoteDeviceEntity ADD COLUMN Model TEXT DEFAULT ''");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Migration warning: Could not add Model column: {ex.Message}");
                }
            }

            if (!hasPublicKeyColumn)
            {
                try
                {
                    db.Execute("ALTER TABLE RemoteDeviceEntity ADD COLUMN PublicKey TEXT");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Migration warning: Could not add PublicKey column: {ex.Message}");
                }
            }
            
            if (!hasSentSftpRequestColumn)
            {
                try
                {
                    db.Execute("ALTER TABLE RemoteDeviceEntity ADD COLUMN HasSentSftpRequest INTEGER DEFAULT 0");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Migration warning: Could not add HasSentSftpRequest column: {ex.Message}");
                }
            }
        }

        if (db.GetTableInfo(nameof(ApplicationInfoEntity)).Count == 0)
        {
            db.CreateTable<ApplicationInfoEntity>();
        }

        if (db.GetTableInfo(nameof(NotificationEntity)).Count == 0)
        {
            db.CreateTable<NotificationEntity>();
        }

        return db;
    }

    // 实现 IDisposable 接口
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // 释放托管资源
                Database?.Dispose();
                Database = null;
            }
            _disposed = true;
        }
    }

    // 析构函数
    ~DatabaseContext()
    {
        Dispose(false);
    }
}
