namespace Loupedeck.ClaudeDeckPlugin
{
    // Logi Plugin Service will not load a plugin assembly that contains no ClientApplication - tried
    // and refused ("Cannot load plugin from …dll", after Load() had already run). This plugin is not
    // about any one application, so the type exists only to satisfy that rule and overrides nothing;
    // Plugin.HasNoApplication is what actually tells the host there is no application to follow.
    public class NoApplication : ClientApplication
    {
    }
}
