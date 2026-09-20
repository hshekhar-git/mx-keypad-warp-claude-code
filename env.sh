# source this before any dotnet work here (install.sh does it for you).
#
# Homebrew's dotnet formula installs outside the default location, so DOTNET_ROOT has to be explicit:
# /opt/homebrew on Apple Silicon, /usr/local on Intel. Microsoft's own installer needs nothing.
export PATH="/opt/homebrew/bin:/usr/local/bin:$HOME/.dotnet/tools:$PATH"
if [ -z "${DOTNET_ROOT:-}" ]; then
  for _d in /opt/homebrew/opt/dotnet/libexec /usr/local/opt/dotnet/libexec; do
    if [ -d "$_d" ]; then export DOTNET_ROOT="$_d"; break; fi
  done
  unset _d
fi
export DOTNET_ROLL_FORWARD=Major
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
