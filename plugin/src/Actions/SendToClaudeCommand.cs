namespace Loupedeck.ClaudeDeckPlugin
{
    using System;

    // A command key configured in Options+ rather than in config.json: drop it on a key, fill in the
    // form. Any number of them can be placed, each with its own text.
    //
    // The form's field names are part of what Options+ stores for a placed key, so they are fixed.
    public class SendToClaudeCommand : ActionEditorCommand
    {
        private static class Field
        {
            public const String Text = "text";
            public const String Submit = "submit";
            public const String Forward = "forward";
            public const String Label = "label";
        }

        public SendToClaudeCommand()
            : base((DeviceType)DeviceTypeAliases.MxCreativeKeypad)
        {
            this.DisplayName = "Send to Claude";
            this.Description = "Types text into the terminal in front, optionally pressing Return";
            this.GroupName = "Claude";
            this.IsWidget = true;

            var form = this.ActionEditor;
            form.AddControlEx(new ActionEditorTextbox(Field.Text, "Text",
                "What to type, exactly as written. Empty, with Return ticked, is a bare Enter - which accepts a plan or a question's highlighted answer."));
            form.AddControlEx(new ActionEditorCheckbox(Field.Submit, "Press Return",
                "Send it straight away. Leave this off for something you finish by hand, like \"/compact \" followed by your own instructions."));
            form.AddControlEx(new ActionEditorCheckbox(Field.Forward, "Bring the target session forward",
                "With no terminal in front, go to the session the keypad is aimed at first. Off: the key does nothing unless a terminal is already in front."));
            form.AddControlEx(new ActionEditorTextbox(Field.Label, "Key label", "Shown on the key. Defaults to the text."));
        }

        private static KeyDef Read(ActionEditorActionParameters form)
        {
            var text = form.GetString(Field.Text, "");
            var label = form.GetString(Field.Label, "").Trim();
            return new KeyDef
            {
                Text = text,
                Submit = form.GetBoolean(Field.Submit, false),
                Label = label.Length > 0 ? label : text.Trim().Length > 0 ? text.Trim() : "send",
            };
        }

        protected override Boolean RunCommand(ActionEditorActionParameters actionParameters)
        {
            var key = Read(actionParameters);
            var anythingToSend = key.Text.Length > 0 || key.Submit;
            return anythingToSend && Deck.Send(key, actionParameters.GetBoolean(Field.Forward, false));
        }

        protected override BitmapImage GetCommandImage(ActionEditorActionParameters actionParameters, Int32 imageWidth, Int32 imageHeight) =>
            TileRenderer.Command(Read(actionParameters).Label, null, imageWidth, imageHeight);

        protected override String GetCommandDisplayName(ActionEditorActionParameters actionParameters) => "";
    }
}
