using Alife.Framework;
using Alife.Function.Browser;
using Alife.Function.FunctionCaller;

var character = new Character {
    Name = "开发测试助手",
    Modules = [
        typeof(OpenAILanguageModel).FullName!,
        typeof(XmlFunctionCaller).FullName!,
        typeof(BrowserService).FullName!
    ]
};
var suite = await DemoSuite.InitializeAsync(character);
await suite.RunAsync();
