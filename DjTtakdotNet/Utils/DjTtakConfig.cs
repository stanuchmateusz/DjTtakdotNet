using Serilog;
using Microsoft.Extensions.Configuration; // Added this using

namespace DjTtakdotNet.Utils;

public class DjTtakConfig : IDjTtakConfig
{
    private readonly IConfigurationRoot _configuration;
    private const string ConfigName = "DjTtak:";
    private const string DeleteMessageTimeoutSettingName = "DeleteMessageTimeout";
    private const string TokenSettingName = "Token";
    private const string MusicRoleIdSettingName = "MusicRoleId";
    private const string InactivityTimeoutSettingName = "InactivityTimeoutMinutes"; // <--- Added this

    public int DeleteMessageTimeout { get; set; }
    public string Token { get; set; }
    public string MusicRole { get; set; }
    public int InactivityTimeoutMilliseconds { get; set; }

    public DjTtakConfig( IConfigurationRoot configuration )
    {
        _configuration = configuration;
        InitConfiguration();
    }

    private void InitConfiguration()
    {
        try
        {
            var timeoutInSecs = _configuration.GetValue<int>( ConfigName + DeleteMessageTimeoutSettingName );
            DeleteMessageTimeout = timeoutInSecs * 1000;

            var timeoutInMinutes = _configuration.GetValue<int>( ConfigName + InactivityTimeoutSettingName ); // <--- Read new setting
            // Ensure timeout is positive, default to 5 minutes if not
            InactivityTimeoutMilliseconds = timeoutInMinutes > 0 ? timeoutInMinutes * 60 * 1000 : 5 * 60 * 1000; // <--- Convert to milliseconds and add validation

            Token = _configuration.GetValue<string>( ConfigName + TokenSettingName );
            MusicRole = _configuration.GetValue<string>( ConfigName + MusicRoleIdSettingName );

            Log.Information("Configuration loaded: DeleteMessageTimeout={DeleteMessageTimeout}ms, InactivityTimeout={InactivityTimeout}ms",
                DeleteMessageTimeout, InactivityTimeoutMilliseconds); // <--- Log the loaded values
        }
        catch (Exception e)
        {
            Log.Error(e, "Fatal config init error!");
            throw;
        }
    }
}