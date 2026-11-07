
$ErrorActionPreference = 'SilentlyContinue'
$cpu = @(Get-CimInstance -ClassName Win32_Processor)[0]
$memory = @(Get-CimInstance -ClassName Win32_PhysicalMemory)[0]
$board = @(Get-CimInstance -ClassName Win32_BaseBoard)[0]
$product = @(Get-CimInstance -ClassName Win32_ComputerSystemProduct)[0]
$system = @(Get-CimInstance -ClassName Win32_ComputerSystem)[0]
@{
    cpu_name = [string]$cpu.Name
    ram_manufacturer = [string]$memory.Manufacturer
    ram_part_number = [string]$memory.PartNumber
    board_manufacturer = [string]$board.Manufacturer
    board_product = [string]$board.Product
    system_uuid = [string]$product.UUID
    total_physical_bytes = [string]$system.TotalPhysicalMemory
} | ConvertTo-Json -Compress
