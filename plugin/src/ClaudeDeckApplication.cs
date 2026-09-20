namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // The host wants a ClientApplication type even from a universal plugin; this one claims nothing.
    public class ClaudeDeckApplication : ClientApplication
    {
        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
