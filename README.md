# 3DS-Puzzle-and-Dragons-X-Translation
- This project was a temporary study and created with the help of AI
- Because of the text control codes are somewhat complex.it's uncertain whether it will be taken further and may be abandoned.
- Confirm and Need to be modified：Archive: CPK，Images: Timg ，Text: MSBT，Video：Moflex
- The font system principle is simple: just modify 00_rod_db.bin, add character mappings, then generate the corresponding texture (and bold texture). However, it still requires patching ARM instructions, shortening gaps, and rearranging to increase the space for the font.
Both the font and text are stored in AllCPKA.CPK under the romfs directory. The images may be scattered and haven't been fully checked.
- The most challenging part of the reverse engineering was modifying the CPK variant, which involved consulting a lot of references. Later repacking of that format were completed, although byte-level alignment could not be achieved.
- Second only to archiving in difficulty is the development of timg texture variants. One must be proficient with the 3DS GPU and the related tile arrangement algorithms, especially handling the lossy compression ETC1 A4, which is quite challenging. At present… the textures exported for testing are correct, and I hope the import will go smoothly.
