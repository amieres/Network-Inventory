$settings = (Get-Content appsettings.json | ConvertFrom-Json)

#CHANGE ME
$slug = 'c6a2317c_netdaemon5' # the slug can be found in the url of the browser when navigating to the NetDaemon addon

$version = $slug.Split('_')[-1] # adapt if you are not using the default foldername for the addon
$json = '{"addon": "' + $slug + '"}'
$ip = $settings.HomeAssistant.Host
$port = $settings.HomeAssistant.Port

$token = $settings.HomeAssistant.Token

# Point to the HA PowerSHell Module
Unblock-File ..\Home-Assistant\Home-Assistant.psd1
Unblock-File ..\Home-Assistant\Home-Assistant.psm1
Import-Module ..\Home-Assistant

New-HomeAssistantSession -ip $ip -port $port -token $token

Invoke-HomeAssistantService -service hassio.addon_stop -json $json

# appsettings.json lives only on the share (CopyToOutputDirectory=Never), so a
# blind wipe deletes it and the add-on then fails to start. Keep a copy and put
# it back after publishing.
$target   = "\\$ip\config\$version"
$settingsPath = Join-Path $target 'appsettings.json'
$savedSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }

# Delete files but leave directories in place: SMB cannot create the
# runtimes\<rid>\native subdirectories on demand during publish, so removing
# them makes the copy fail.
Get-ChildItem -Path $target -Recurse -File | Remove-Item -Force -ErrorAction SilentlyContinue

echo "dotnet publish -c Release -o \\$ip\config\$version"
dotnet publish -c Release -o \\$ip\config\$version

if ($savedSettings -and -not (Test-Path $settingsPath)) {
    echo "restoring appsettings.json"
    Set-Content -Path $settingsPath -Value $savedSettings -NoNewline
}

#echo "copy bin\Release\net9.0\publish\* \\$ip\config\$version\"
#copy bin\Release\net9.0\publish\* \\$ip\config\$version\

Invoke-HomeAssistantService -service hassio.addon_start -json $json

