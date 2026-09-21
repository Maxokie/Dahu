# Translating Dahu

Each file in this folder is one language. The file name is the language code (`en.json`, `fr.json`, `de.json`, `pt-br.json`...).

## Add a language

1. Copy `en.json` and rename the copy to your language code, for example `de.json`.
2. Set `"Lang.Name"` to the name of the language as it should appear in the settings (`"Deutsch"`).
3. Translate the text on the right side of each line. Never change the keys on the left side.
4. Open a pull request with your file. Nothing else has to be changed: every `*.json` file in this folder is built into Dahu automatically and shows up in Settings > Language.

## Rules

- Keep placeholders such as `{0}`, `{1}` or `{1:0.0}` exactly as they are. They are replaced by values at runtime.
- Keep `\n` (line break) and `\\` (backslash) sequences.
- A key that is missing from your file falls back to English, so a partial translation still works.
- Save the file as UTF-8.

## Try your translation without building

Create a folder named `lang` next to `Dahu.exe`, put your file in it and restart Dahu. Files found there are loaded on top of the built-in languages, so you can also use it to override a few strings of an existing language.
