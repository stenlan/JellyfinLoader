import {readFile, writeFile} from "fs/promises";

function regexReplace(str, regex, replacer) {
    const matchArray = regex.exec(str);
    const [fullMatch, ...groups] = matchArray;

    return str.substring(0, matchArray.index) + replacer(...groups) + str.substring(matchArray.index + fullMatch.length);
}

function increaseVersion(versionString, increaseType) {
    increaseType = increaseType?.toLowerCase();

    const splitVersion = versionString.split(".");
    const index = ["major", "minor", "patch"].indexOf(increaseType);

    if (index === -1) throw new Error(`Unknown increase type "${increaseType}".`);

    splitVersion[index] = parseInt(splitVersion[index]) + 1;

    for (let i = index + 1; i < splitVersion.length; i++) {
        splitVersion[i] = 0;
    }

    return splitVersion.join(".");
}

const increaseType = process.argv[2];
const metaPath = "meta.json";

let metaJSON = JSON.parse(await readFile(metaPath, "utf-8"));
let newVersion = increaseVersion(metaJSON.version, increaseType);
metaJSON.version = newVersion;

await writeFile(metaPath, JSON.stringify(metaJSON, null, 2));

for (const projFile of ["JellyfinLoader/JellyfinLoader-common.csproj", "JLTrampoline/JLTrampoline-common.csproj"]) {
    await writeFile(projFile, regexReplace(await readFile(projFile, "utf-8"), /<VersionPrefix>(.+?)<\/VersionPrefix>/, () => newVersion));
}

console.log(newVersion);