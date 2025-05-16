using Serilog;

namespace DjTtakdotNet.Utils;

public class DjTtakConfig : IDjTtakConfig
{
    private const string ConfigName = "DjTtak:";
    private const string DeleteMessageTimeoutSettingName = "DeleteMessageTimeout";
    private const string TokenSettingName = "Token";
    private const string MusicRoleIdSettingName = "MusicRoleId";
    private const string InactivityTimeoutSettingName = "InactivityTimeout";

    private readonly IConfigurationRoot _configuration;

    public DjTtakConfig(IConfigurationRoot configuration)
    {
        _configuration = configuration;
        InitConfiguration();
    }

    public int DeleteMessageTimeout { get; private set; }
    public string? Token { get; private set; }
    public string? MusicRole { get; private set; }
    public int InactivityTimeoutMilliseconds { get; private set; }

    private void InitConfiguration()
    {
        try
        {
            var delMessTimeout = _configuration.GetValue<int>(ConfigName + DeleteMessageTimeoutSettingName);
            if (delMessTimeout < 0)
                throw new InvalidConfigException("DeleteMessageTimeout cannot be negative");

            DeleteMessageTimeout = delMessTimeout * 1000;

            var inactivityTimeout = _configuration.GetValue<int>(ConfigName + InactivityTimeoutSettingName);
            if (inactivityTimeout < 0)
                throw new InvalidConfigException("InactivityTimeout cannot be negative");

            InactivityTimeoutMilliseconds = inactivityTimeout * 1000;

            Token = _configuration.GetValue<string>(ConfigName + TokenSettingName) ??
                    throw new InvalidConfigException("Token cannot be null");

            if (Token.Length == 0)
                throw new InvalidConfigException("Token cannot be empty");

            if (Token == "YOUR BOT TOKEN HERE")
                throw new InvalidConfigException("Please setup the config file, modify appsettings.json");

            MusicRole = _configuration.GetValue<string>(ConfigName + MusicRoleIdSettingName) ??
                        throw new InvalidOperationException("Token cannot be null");

            Log.Information(
                "Configuration loaded: DeleteMessageTimeout={DeleteMessageTimeout}ms, InactivityTimeout={InactivityTimeout}ms",
                DeleteMessageTimeout, InactivityTimeoutMilliseconds);
        }
        catch (Exception ex)
        {
            if (ex is FormatException or ArgumentException)
                throw new InvalidConfigException("Invalid configuration format, make sure to use correct types", ex);

            Log.Error(ex, "Fatal config init error!");
            throw;
        }
    }
}

public class InvalidConfigException : Exception
{
    public InvalidConfigException() : base("Error while loading configuration")
    {
    }

    public InvalidConfigException(string message) : base(message)
    {
    }

    public InvalidConfigException(string message, Exception inner) : base(message, inner)
    {
    }
}