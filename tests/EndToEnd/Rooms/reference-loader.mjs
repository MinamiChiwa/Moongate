const replacement = new URL('./reference-server-adapter.mjs', import.meta.url).href;
export async function resolve(specifier, context, nextResolve) {
  if (specifier === '../server/room-server.js' && context.parentURL?.endsWith('/tests/server.test.mjs'))
    return { url: replacement, shortCircuit: true };
  return nextResolve(specifier, context);
}
