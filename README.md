# CharM
CharM is a TTRPG character creation program, compatible with rules defining the 4th Edition of the worlds most popular RPG

## Usage
### Windows
Download the latest windows build from the releases page and extract the charm-maui folder. Run charm.maui.exe.
### Mac
Run Charm. It is a little larger than it has to be because something in trimming breaks the database, but it should run. Testing is more limited annoyingly because of a lack of apple devices.
### Android
Install the APK. The android version doesn't support building a rules db from the exe because of compression library compatibility issues, so bring a rules db.
### Linux
Because Linux isn't an OS, its a kernel with a whole bunch of tools in a trenchcoat, getting a unified UI option that works for everyone is just not going to happen. So on Linux you get to bring your own browser. Use the Linux server release and run it (this may require chmod +x) as a daemon with --urls "http://localhost:8080" (or whatever port and bind you please, I'm not a cop). Then open that url in your browser of choice. Because C libraries are a pre-AI hallucination we all just have to live with you might have have to add or delete some packages from whatever distro you are using. If you figure out what those are and want to package this up so it works with whatever your package installer of choice is, go nuts. 

### Server Mode
If you're an unhinged lunatic who for some reason wants to run this as a webserver on platforms where you don't have to, you can. Extract the server release for your OS (Windows or Linux) and run it with the urls option defining what you want it to bind on. Please note that I am not responsible if something bad happens because you exposed a character maker server to the internet. Kestrel is a competent webserver, and I know what the code involved here is doing, but I can make no assurances that 0 days won't happen or libraries won't get outdated and leave you vulnerable. Getting your computer infected because you exposed your retro RPG character maker to the internet is considered embarrassing in most nerd circles.

### Importing rules
The first time you run CharM you'll be prompted to point it at your "rules.db" file. If you've run charm before, you should already have one that you've backed up. If you don't, that's ok, CharM can create one. You need an item from column A, and optionally an item from column B.

| A | B |
|--|--|
|The last CharacterBuilderUpdate.exe | the url for CBLoader's wotc.index (or any other complete index file) |
| A combined.dnd40.merged.xml | A folder full of CBLoader part files |

If you don't know where you left those things, I am sure there are people on the internet willing to help you. Once you've created your rules.db file, make sure to back it up somewhere safe so you don't need to do this again in the future. Creating the rules db can take a while, especially if you're pulling an index from the internet.

Charm is technically compatible with anything else using the same D20Engine rules XML standard (at least to a point), but I'm not aware of anything else yet. Someone should do it for Orcus, Amethyst, and/or 13th Age because that'd be really cool. Almost anything using SRD would be possible to some degree, though the capabilities might be a BIT wasted on more "old school" rpgs.

### Creating and editing characters

On a character with unselected choices (like a new character or a character you levelled up), there should be some boxes at the top of the page indicating which choices are available. You can click them to select from the available options given the choice and your characters current state. If you don't like a choice you made, you can find all of them in the detail tab and remove it, some can also be reselected by clicking the little edit pencil right by the choice. Optional choices can be dismissed with the little x after them. Some choices may be selectable multiple times (the choice box will have a number after it if it is). 

## Features
- All 4th edition rulesxml elements covered (AFAICT) with close to perfect parity*
- Modern UI for use as a digital character sheet
- Special Print mode (including power cards)
- Custom CSS (click the gear in the top right) for making the layout how you like
- DND4E format importing and exporting
- Text String/Forum Post import/export
- Building a character from scratch
- Levelling up a character you'd already built before
- Basic "digital character sheet" features so you can use a tablet or laptop at the table.

(*) There are some things we've deliberately deviated on because the outputs from other rules xml parsers were missing some conditions or didn't output things in as human friendly of a way as they could have. There are also some corrections we've made based on changes made after the last character builder version (starting funds post-level 1 for example)

### Things that aren't done:
- Rules version stamping for characters (will be useful if you're trying to tell if you made a character with specific homebrew parts/versions)
- Customizable parts file loads/updates/enables/disables
- Better file format options than the DND4e one
- Retargeted compendium links for use with live options
- A decent icon
- Heavy optimization (probably not happening, see notes)
- The print UI couold probably be better or support some sort of templating engine.

### Notes
CharM is implemented as a webUI first and foremost with a platform specific wrapper provided by dotnet maui. This is because there are no good unified cross platform UI toolkits, and because you will likely want to print your character sheet or export a pdf at some point, and supporting that sounds hellish. Every OS worth mentioning has a browser that can already do that. This does mean it is not as efficient on computer resources as it could be, if you are concerned about the resource usage of the Character Making program for a TTRPG I strongly encourage you to re-examine your life priorities. Or make a PR. Or just steal this code for your better character maker, you can do that, its fine with me.

There is no internationalization, I don't know if rules XML in other languages exists, I very much doubt it based on how the rules engine often resorted to literal text parsing, but if someone has them and wants to try them I'd love to hear what happens, it probably means MANY code changes are needed to the power card logic to support it.

I've included some of the research docs in case anyone else wants to work with the file formats in question that CharM deals with. Some of them are AI written, human reviewed, some of them are me written. The XSD files for rules xml and dnd4e files are SPECIFICALLY very useful if you're trying to grasp the structure of how those formats work (DND4e files often have useful xml comments but the format is still somewhat poorly laid out).  

## Contributing
Feel free to fork this project, do what you want with it. If you just want a little change, feel free to make a PR back. If you want a big change, you can but be aware it might be rejected. I do have an awful lot of tests, but you might notice they are absent. Because this program is so driven by the rules db it uses and because I can't reistribute said db, the tests will be basically nonfunctional until provided with one. Once I've figured out what I want to do about that, I might share those and make them a pre-req, IDK yet.

### Reporting a bug
There's a great chance I've missed some weird rules interaction or wording quirk that the OCB handled (or worse didn't handle). If you want me to look into a "thing" you think is wrong please provide the dnd4e character file in question as well as details about what your expected behavior is vs observed behavior. You should also provide you short rules db hash.

## AI Usage disclaimer
This project has a lot of AI written/assisted code. I am a developer by trade, I know what the code is doing well enough and I've rewritten/reworked enough portions of it that I'm not concerned, but obviously you have your own comfort level. This project specifically was a particularly good use case for an AI loop since we have very distinct inputs we can recalculate with our xml parser/rules interpreter that can then be judged for objective correctness against the original, so slop has to actually work to move forward. The tedious process of analyze large dataset, make small code change, reanalyze and score large dataset is one that computers are objectively meant to do. Also the AI makes prettier frontends than I do.
