using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace Custodian.System.Tests;

public abstract class TestBase : IDisposable
{
    protected readonly IWebDriver Driver;
    protected readonly WebDriverWait Wait;
    protected const string BaseUrl = "http://localhost:3000";

    protected TestBase()
    {
        var options = new ChromeOptions();
        // options.AddArgument("--headless=new"); // Uncomment to run without opening a browser window
        options.AddArgument("--window-size=1440,900");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        try
        {
            Driver.Quit();
            Driver.Dispose();
        }
        catch
        {
            // Ignore teardown errors
        }
    }
}

