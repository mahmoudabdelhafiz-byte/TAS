// Guide files imported as text (Vite's ?raw).
declare module '*.md?raw' {
  const text: string;
  export default text;
}
