// avamux's pi extension, loaded with `pi -e` when avamux runs pi in RPC mode (AI Chat, AI Actions).
// It lets the model ask the user a question (the ask_user tool) and, when avamux is set to, asks before tools run.
// The questions arrive in avamux as extension UI requests whose title starts with a marker avamux recognises.
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";

const QUESTION = "[avamux:question]";
const PERMISSION = "[avamux:permission]";

export default function (pi: ExtensionAPI) {
  pi.registerTool({
    name: "ask_user",
    label: "Ask the user",
    description:
      "Ask the user a question and wait for the answer. Use it when you need a decision or a fact only the user has. " +
      "Give 2 to 4 short options when the answer is a choice; the user can always type something else.",
    parameters: Type.Object({
      question: Type.String({ description: "The question, as one clear sentence." }),
      options: Type.Optional(Type.Array(Type.String(), { description: "Answers to choose from (2 to 4)." })),
      multiSelect: Type.Optional(Type.Boolean({ description: "Whether several options may be chosen." })),
    }),
    async execute(_id, params, _signal, _onUpdate, ctx) {
      const payload = JSON.stringify({ question: params.question, options: params.options ?? [], multiSelect: !!params.multiSelect });
      const answer = (params.options && params.options.length)
        ? await ctx.ui.select(QUESTION + payload, params.options)
        : await ctx.ui.input(QUESTION + payload, "");
      return { content: [{ type: "text", text: answer == null ? "The user did not answer." : `The user answered: ${answer}` }], details: {} };
    },
  });

  // Tools to ask about first: "*" for all, or names separated by commas. Empty: never ask (the default).
  const confirm = (process.env.AVAMUX_PI_CONFIRM ?? "").split(",").map(s => s.trim()).filter(Boolean);
  if (confirm.length) {
    pi.on("tool_call", async (event, ctx) => {
      if (event.toolName === "ask_user") return;
      if (!confirm.includes("*") && !confirm.includes(event.toolName)) return;
      const payload = JSON.stringify({ tool: event.toolName, input: event.input ?? {} });
      const choice = await ctx.ui.select(PERMISSION + payload, ["Allow", "Allow for this session", "Deny"]);
      if (choice === "Allow" || choice === "Allow for this session") return;
      return { block: true, reason: "The user declined this." };
    });
  }
}
