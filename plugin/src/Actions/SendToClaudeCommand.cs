namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // The GUI-configured twin of a config.json key: text, a Return checkbox and a label, set in the
    // Options+ form. Drop it on as many keys as you like.
    public class SendToClaudeCommand : ActionEditorCommand
    {
        private const String TextName = "text";
        private const String SubmitName = "submit";
        private const String LabelName = "label";
        private const String ForwardName = "forward";

        public SendToClaudeCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.DisplayName = "Send to Claude";
            this.Description = "Types text into the terminal in front, optionally pressing Return";
            this.GroupName = "Claude";
            this.IsWidget = true;

            this.ActionEditor.AddControlEx(new ActionEditorTextbox(
                TextName, "Text", "Typed as written. Leave empty and tick Return for a bare Enter, which accepts a plan or a question."));
            this.ActionEditor.AddControlEx(new ActionEditorCheckbox(
                SubmitName, "Press Return", "Submit straight away. Leave off for commands you add to, such as /compact."));
            this.ActionEditor.AddControlEx(new ActionEditorCheckbox(
                ForwardName, "Bring the selected session forward", "If no terminal is in front, focus the session last selected on the keypad first."));
            this.ActionEditor.AddControlEx(new ActionEditorTextbox(
                LabelName, "Key label", "What the key shows. Defaults to the text."));
        }

        protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
        {
            var key = new KeyDef
            {
                Text = actionParameters.GetString(TextName, ""),
                Submit = actionParameters.GetBoolean(SubmitName, false),
            };
            return (key.Text.Length > 0 || key.Submit)
                && Deck.Send(key, actionParameters.GetBoolean(ForwardName, false));
        }

        protected override BitmapImage GetCommandImage(ActionEditorActionParameters actionParameters, Int32 imageWidth, Int32 imageHeight)
        {
            var label = actionParameters.GetString(LabelName, "");
            if (label.Length == 0)
            {
                label = actionParameters.GetString(TextName, "").Trim();
            }

            return TileRenderer.Command(label.Length > 0 ? label : "send", null, imageWidth, imageHeight);
        }

        protected override String GetCommandDisplayName(ActionEditorActionParameters actionParameters) => "";
    }
}
