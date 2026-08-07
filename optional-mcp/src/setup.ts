import { randomBytes } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = fileURLToPath(new URL("../", import.meta.url));
const templatePath = resolve(projectRoot, "config.template.jsonc");
const requestedPath = process.argv[2] || "config.jsonc";
const outputPath = resolve(projectRoot, requestedPath);
const instanceId = randomBytes(4).toString("hex");

let template = await readFile(templatePath, "utf8");
template = template.replaceAll("{{INSTANCE_ID}}", instanceId);

await mkdir(dirname(outputPath), { recursive: true });
try {
  await writeFile(outputPath, template, { encoding: "utf8", flag: "wx" });
} catch (error) {
  if ((error as NodeJS.ErrnoException).code === "EEXIST") {
    throw new Error(`Refusing to replace existing configuration: ${outputPath}`);
  }
  throw error;
}

console.log(`Created ${outputPath}`);
console.log(`Instance ID: ${instanceId}`);
console.log(`Suggested MCP registration name: ask-larger-model-${instanceId}`);
console.log("Uncomment only the optional features wanted for this installation.");
