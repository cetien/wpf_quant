$base = "C:\Users\tien7\source\repos\quant\build"
Add-Type -Path "$base\LiveChartsCore.dll"
Add-Type -Path "$base\LiveChartsCore.SkiaSharpView.dll"

$asm1 = [System.AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq "LiveChartsCore" }
$asm2 = [System.AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq "LiveChartsCore.SkiaSharpView" }

Write-Host "=== DrawMarginFrame types ==="
($asm1.GetTypes() + $asm2.GetTypes()) | Where-Object { $_.Name -like "*DrawMargin*" -or $_.Name -like "*Margin*" } | ForEach-Object {
    Write-Host $_.FullName
    $_.GetConstructors() | ForEach-Object {
        $params = $_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }
        Write-Host "  ctor($($params -join ', '))"
    }
}
