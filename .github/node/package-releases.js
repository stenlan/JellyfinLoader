import {readFile, writeFile, readdir } from "fs/promises";
import archiver from "archiver";
import { createHash } from "crypto";
import { createReadStream, createWriteStream } from "fs";
import { once } from "events";

function createZipFile(fileName, adder) {
    const archive = archiver('zip');
    adder(archive);
    const writeStream = createWriteStream(fileName);
    const res = once(writeStream, 'close');
    archive.pipe(writeStream);
    archive.finalize();

    return res;
}

function md5sum(path) {
  // const md5 = await md5sum(path)
  // https://stackoverflow.com/a/44643479/10440128
  return new Promise((resolve, reject) => {
    const hash = createHash('md5')
    const stream = createReadStream(path)
    stream.on('error', err => reject(err))
    stream.on('data', chunk => hash.update(chunk))
    stream.on('end', () => resolve(hash.digest('hex')))
  });
}

const newVersion = process.env.newVersion;
const distFolder = "./dist";
const jfVersions = await readdir(distFolder);
const metaPath = "./meta.json";
const metaJSON = JSON.parse(await readFile(metaPath, "utf-8"));
const newVersions = [];

for (const jfVersion of jfVersions) {
    const sharedVersionPath = `JellyfinLoader/build/${jfVersion}/bin/dep/tree/SharedVersion.cs`;
    const assemblyVersion = (await readFile(sharedVersionPath, "utf-8")).match(/AssemblyVersion\("([\d.]+?)"\)/)[1];
    const fullAssemblyVersion = assemblyVersion + ".0";
    const newMetaJSON = {...metaJSON};
    newMetaJSON.version = newVersion + ".0";
    newMetaJSON.targetAbi = fullAssemblyVersion;
    newMetaJSON.timestamp = new Date().toISOString();

    await writeFile(`${distFolder}/${jfVersion}/meta.json`, JSON.stringify(newMetaJSON, null, 2));

    const zipName = `JellyfinLoader-${newVersion}-${jfVersion}.zip`;
    const outputPath = `${distFolder}/${zipName}`;
    await createZipFile(outputPath, (archive) => archive.directory(`${distFolder}/${jfVersion}/`));

    const md5Hash = await md5sum(outputPath);
    newVersions.push({
        "checksum": md5Hash,
        "changelog": "",
        "targetAbi": newMetaJSON.targetAbi,
        "sourceUrl": `https://github.com/stenlan/JellyfinLoader/releases/download/v${newVersion}/${zipName}`,
        "timestamp": newMetaJSON.timestamp,
        "version": newMetaJSON.version
    });
}

const repoPath = "./repository.json";
const repoJSON = JSON.parse(await readFile(repoPath, "utf-8"));

repoJSON.find(obj => obj.name === "JellyfinLoader").versions.unshift(...newVersions);
await writeFile(repoPath, JSON.stringify(repoJSON, null, 2));