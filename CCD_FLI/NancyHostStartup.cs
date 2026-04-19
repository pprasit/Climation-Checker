using Nancy;
using Nancy.Conventions;

namespace CCD_FLI
{
    public class NancyHostStartup : DefaultNancyBootstrapper
    {
        protected override void ConfigureConventions(NancyConventions nancyConventions)
        {
            nancyConventions.StaticContentsConventions.Add(StaticContentConventionBuilder.AddDirectory("/CCD_TEMP", @"CCD_TEMP"));
            nancyConventions.StaticContentsConventions.Add(StaticContentConventionBuilder.AddDirectory("/CCD_FITS", @"CCD_FITS"));
            
            base.ConfigureConventions(nancyConventions);
        }
    }
}