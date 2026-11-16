# Publish to Steam Workshop

The Workshop title is read from the module name in SubModule.xml. The first-upload template defaults to **Friends Only**.

1. Install the release module and enable it in the Bannerlord launcher. From the **Bannerlord Modding Kit**, open **Editor → File → Publish Module**, select **Bannerlord Together Crash Guard**, select **Client**, and export to a writable folder.
2. Copy WorkshopCreate.xml outside the exported module. Set its ModuleFolder to the folder that directly contains SubModule.xml. Set Image to the absolute path of docs/cover.png in this repository. The preview must be under 1 MB.
3. Use the Steam account that will own the item. Make sure Steam Cloud is enabled for Bannerlord. For a **first upload** run the following in PowerShell, substituting your paths. For an existing item, use TaleWorlds' update template with its Workshop item ID.

```powershell
Set-Location -LiteralPath 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client'
.\TaleWorlds.MountAndBlade.SteamWorkshop.exe 'C:\path\to\WorkshopCreate.xml'
```

4. Check the listing title and visibility in Steam. Add **Harmony** and **BannerlordTogether** as required items and share the Workshop link. To test the Workshop copy, move the manual installation out of the game's Modules folder and subscribe to the item.

[TaleWorlds Workshop documentation](https://moddocs.bannerlord.com/steam-workshop/uploading_updating_mod/)
