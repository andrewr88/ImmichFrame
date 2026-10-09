namespace ImmichFrame.Core.Exceptions
{
    public class ImmichFrameException : Exception
    {
        public ImmichFrameException() { }
        public ImmichFrameException(string? message) : base(message) { }
        public ImmichFrameException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    public class AssetNotFoundException : ImmichFrameException
    {
        public AssetNotFoundException() : base() { }
        public AssetNotFoundException(string message) : base(message) { }
        public AssetNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ProfileNotFoundException : ImmichFrameException
    {
        public ProfileNotFoundException() : base() { }
        public ProfileNotFoundException(string message) : base(message) { }
        public ProfileNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }
    /// <summary>
    /// A configuration save refused before anything was written, because the stored settings are no
    /// longer the ones the save was based on - which the operator resolves by reloading the editor
    /// and applying the change again. Distinct from <see cref="SettingsNotValidException"/>, which
    /// means the configuration itself is wrong. On a save it means one of:
    /// <list type="bullet">
    /// <item>A stale version token: another save landed after the editor read the settings.
    /// <c>AdminConfigService.Save</c> checks the editor's token against the stored version, and
    /// <c>SettingsService.Save</c> checks the version its caller read against the stored row's.</item>
    /// <item>A concurrent writer: another process sharing the settings database updated the row
    /// while this save was in flight, which <c>SettingsService.Save</c> learns from EF's concurrency
    /// check on the row's version.</item>
    /// </list>
    /// <para>
    /// It is also thrown outside the save path, by <c>ConfigLoader.DescribeSource</c> through
    /// <c>ConfigLoader.Unreadable</c>, for a settings file that is present but cannot be read in
    /// either the current or the old schema. The first-run import that calls it catches it, logs the
    /// message and starts unconfigured.
    /// </para>
    /// </summary>
    public class ConfigSaveRefusedException : ImmichFrameException
    {
        public ConfigSaveRefusedException() : base() { }
        public ConfigSaveRefusedException(string message) : base(message) { }
        public ConfigSaveRefusedException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SettingsNotValidException : ImmichFrameException
    {
        public SettingsNotValidException() : base() { }
        public SettingsNotValidException(string message) : base(message) { }
        public SettingsNotValidException(string message, Exception innerException) : base(message, innerException) { }
    }
}
