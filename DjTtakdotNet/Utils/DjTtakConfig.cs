using Serilog;

namespace DjTtakdotNet.Utils;

public class DjTtakConfig : IDjTtakConfig
{
    private readonly IConfigurationRoot _configuration;
    private const string ConfigName = "DjTtak:";
    private const string DeleteMessageTimeoutSettingName = "DeleteMessageTimeout";
    private const string TokenSettingName = "Token";
    private const string MusicRoleIdSettingName = "MusicRoleId";
    
    public int DeleteMessageTimeout { get; set; }
    public string Token { get; set; }
    public string MusicRole { get; set; }

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
            Token = _configuration.GetValue<string>( ConfigName + TokenSettingName );
            MusicRole = _configuration.GetValue<string>( ConfigName + MusicRoleIdSettingName );
            
        }
        catch (Exception e)
        {
            Log.Error(e, "Fatal config init error!");
        }
    }


    
}