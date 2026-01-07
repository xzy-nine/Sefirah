namespace Sefirah;
public static class Constants
{
    public static class Notification
    {
        public const string FileTransferGroup = "file-transfer";
    }

    public static class ToastNotificationType
    {
        public const string FileTransfer = "FileTransfer";
        public const string RemoteNotification = "RemoteNotification";
        public const string Clipboard = "Clipboard";
    }
    public static class LocalSettings
    {
        public const string DateTimeFormat = "datetimeformat";

        public const string SettingsFolderName = "settings";
        public const string UserSettingsFileName = "user_settings.json";
        public const string DatabaseFileName = "notifyrelay.db";
        public static readonly string ConnectionString = $"Filename={Path.Combine(ApplicationData.Current.LocalFolder.Path, DatabaseFileName)}";
    }

    public static class ExternalUrl
    {
        public const string ReleasesUrl = @"https://github.com/xzy-nine/NotifyRelay-pc/releases/latest";
        public const string AndroidGitHubRepoUrl = @"https://github.com/xzy-nine/Notification-Relay";
        public const string GitHubRepoUrl = @"https://github.com/xzy-nine/NotifyRelay-pc";
        public const string FeatureRequestUrl = @"https://github.com/xzy-nine/NotifyRelay-pc/issues/new?template=request_feature.yml";
        public const string BugReportUrl = @"https://github.com/xzy-nine/NotifyRelay-pc/issues/new?template=report_issue.yml";
        public const string PrivacyPolicyUrl = @"https://github.com/xzy-nine/NotifyRelay-pc/blob/master/.github/Privacy.md";
        public const string LicenseUrl = @"https://github.com/xzy-nine/NotifyRelay-pc/blob/master/LICENSE";
    }

    public static class UserEnvironmentPaths
    {
        public static readonly string DownloadsPath = GetDownloadsPath();
        public static readonly string UserProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string DefaultRemoteDevicePath = Path.Combine(UserProfilePath, "RemoteDevices");
        private static string GetDownloadsPath()
        {
            string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homePath, "Downloads");
            
        }
    }
}
