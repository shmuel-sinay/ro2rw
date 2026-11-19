# Validates a generated SP Flash Tool console config against the console_mode.xsd shipped with SP Flash Tool.
# Usage: powershell -File tests\validate-sp-config.ps1 -Xml sp_readback_config.xml -Xsd <SP dir>\console_mode.xsd
param([Parameter(Mandatory)]$Xml, [Parameter(Mandatory)]$Xsd)
$set = New-Object System.Xml.Schema.XmlSchemaSet
[void]$set.Add($null, $Xsd)
$doc = New-Object System.Xml.XmlDocument
$doc.Schemas = $set
$doc.Load($Xml)
$script:errs = @()
$doc.Validate({ param($s, $e) $script:errs += $e.Message })
if ($script:errs.Count -eq 0) { "VALID $Xml" } else { "INVALID $Xml"; $script:errs; exit 1 }
