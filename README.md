# se7en Source

Change l'entrée de ton écran en un clic (PC ↔ PS5, Switch, box…), dans le design **Centrale** de se7en Voice :

- **Une tuile par entrée** de chaque écran (DisplayPort 1, HDMI 1…), lue dans la chaîne de capacités DDC/CI de
  l'écran ; l'entrée affichée est en blanc. Un clic : l'écran passe dessus.
- **Noms** : « PC », « PS5 »… à la place de « HDMI 1 ».
- **Raccourcis globaux** : Ctrl+Alt+Maj+1…9 pour chaque entrée, Ctrl+Alt+Maj+0 pour revenir à l'entrée d'avant
  (utile quand l'écran affiche la console et qu'on ne voit plus Windows).
- **Essai aller-retour** : l'écran passe 8 s sur une entrée puis revient tout seul, pour vérifier que l'écran répond
  encore une fois sur une autre entrée.
- **Diagnostic** (bouton, ou `--diagnostic`) : état DDC/CI de chaque écran, test de chaque prise, test avec les boutons de
  l'écran (relevé de l'entrée toutes les 0,5 s), conclusion en clair (« l'écran ne reçoit aucune image de la console »…)
  et rapport à copier.
- Apprentissage : une prise affichée avec les boutons de l'écran est retenue (avec le code exact que l'écran utilise).
- Icône près de l'horloge (menu des entrées), lancement avec Windows, une seule instance.

L'ordre passe par DDC/CI (code VCP 0x60, norme MCCS) avec l'API Windows `dxva2` : aucun pilote, aucun droit admin.

## Structure
- `src/Se7enSource.Core` — entrées d'après la chaîne de capacités, noms MCCS, réglages (noms, entrée d'avant). Testé.
- `src/Se7enSource` — app WPF : DDC/CI (`Services/Ddc.cs`), raccourcis globaux, icône de notification, interface.
- `tests/Se7enSource.Tests` — tests xUnit (dont la chaîne réelle d'un AOC AG276QZD2).
- `tools/make_icon.py` — icône (« 7 » Michroma, deux prises reliées par une flèche).

## Commandes
```bash
dotnet test tests/Se7enSource.Tests
```
```bash
dotnet publish src/Se7enSource -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/publish
```

Le guide pour les utilisateurs est dans `dist/publish/LISEZMOI.txt`. Les réglages sont stockés dans `%AppData%\Se7enSource`.

Polices : Michroma, Outfit, IBM Plex Mono (SIL Open Font License, `Assets/Fonts/OFL.txt`).
