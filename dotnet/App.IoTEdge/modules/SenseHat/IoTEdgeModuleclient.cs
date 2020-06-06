using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Sense.RTIMU;
using Newtonsoft.Json;

namespace RP4SenseHat.csharp
{
    public class IoTEdgeModuleClient
    {
        // A wrapper for Azure IoT Edge Module Client class.
        // 1. Connects to IoT Hub/IoT Central using authentication information from DPS
        // 2. Set up callback functions for connection status change, Desired Property change (Settings from Cloud), and commands
        // 3. Reads sensor data from SenseHat
        // 4. Sends telemetry to IoT Hub/IoT Central
        //
        // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.devices.client.moduleclient?view=azure-dotnet
        private ModuleClient _client;
        private static TransportType s_transportType = TransportType.Amqp;
        private bool _bCelcius = true;
        private bool _isPi = true;

        public IoTEdgeModuleClient()
        {
            Console.WriteLine("IoT Hub module client initialized.");
            _bCelcius = true;
        }

        public async Task Initialize()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only)  // must be Mqtt_WebSocket_Only or Mqtt_Tcp_Only
            {
                //CleanSession = true,
                // set proxy etc
            };
            ITransportSettings[] settings = { mqttSetting };
            _client = await ModuleClient.CreateFromEnvironmentAsync(settings);
            _client.ProductInfo = "IoTEdgeModuleSample";
            await _client.OpenAsync();
        }

        public async Task Run()
        {
            // runs loop to send telemetry

            // set up a callback for connection status change
            _client.SetConnectionStatusChangesHandler(ConnectionStatusChangeHandler);

            // set up callback for Desired Property update (Settings/Properties in IoT Central)
            await _client.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyChangedAsync, null).ConfigureAwait(false);

            // set up callback for Methond (Command in IoT Central)
            await _client.SetMethodHandlerAsync("displayMessage", DisplayMessage, null).ConfigureAwait(false);

            Twin twin = await _client.GetTwinAsync().ConfigureAwait(false);
            Console.WriteLine("\r\nDevice Twin received:");
            Console.WriteLine($"{twin.ToJson(Newtonsoft.Json.Formatting.Indented)}");

            if (twin.Properties.Desired.Contains("isCelcius"))
            {
                _bCelcius = twin.Properties.Desired["isCelcius"]["value"];
            }

            if (_isPi)
            {
                // read sensor data from SenseHat
                using (var settings = RTIMUSettings.CreateDefault())
                using (var imu = settings.CreateIMU())
                using (var humidity = settings.CreateHumidity())
                using (var pressure = settings.CreatePressure())
                {
                    while (true)
                    {
                        var imuData = imu.GetData();
                        var humidityReadResult = humidity.Read();

                        if (humidityReadResult.HumidityValid && humidityReadResult.TemperatureValid)
                        {
                            string buffer;
                            // format telemetry based on settings from Cloud
                            if (_bCelcius)
                            {
                                buffer = $"{{\"humidity\":{humidityReadResult.Humidity:F2},\"tempC\":{humidityReadResult.Temperatur:F2}}}";
                            } else
                            {
                                double fahrenheit = (humidityReadResult.Temperatur * 9 / 5) + 32;
                                buffer = $"{{\"humidity\":{humidityReadResult.Humidity:F2},\"tempF\":{fahrenheit:F2}}}";
                            }

                            using (var eventMessage = new Message(Encoding.UTF8.GetBytes(buffer)))
                            {
                                Console.WriteLine(buffer);
                                await _client.SendEventAsync("SensorsOutput", eventMessage).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Err : Sensor data not valid");
                        }

                        await Task.Delay(3 * 1000);
                    }
                }
            } else
            {
                Console.WriteLine($"CTRL+C to exit");
                while (true)
                {
                    await Task.Delay(3 * 1000);
                }
            }
        }

        private void ConnectionStatusChangeHandler(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            // callback when connection to IoT Hub/IoT Central changed
            Console.WriteLine();
            Console.WriteLine($"Connection status changed to {status}.");
            Console.WriteLine($"Connection status changed reason is {reason}.");
            Console.WriteLine();
        }
        
        private Task<MethodResponse> DisplayMessage(MethodRequest methodRequest, object userContext)
        {
            // callback when a command is sent from cloud
            Console.WriteLine($"\r\n*** {methodRequest.Name} was called.");
            Console.WriteLine("{0}", methodRequest.DataAsJson);

            // display on SenseHat LED
            Sense.Led.LedMatrix.ShowMessage(methodRequest.DataAsJson.Replace("\"", string.Empty).Trim());

            // return response to IoT Hub/IoT Central
            return Task.FromResult(new MethodResponse(new byte[0], 200));
        }

        private async Task OnDesiredPropertyChangedAsync(TwinCollection desiredProperties, object userContext)
        {
            // callback when Desired Property (settings) is changed/updated by Cloud/Backend
            Console.WriteLine("\r\nDesired property (Settings) changed:");
            Console.WriteLine($"{desiredProperties.ToJson(Newtonsoft.Json.Formatting.Indented)}");


            // IoT Central expects the following payloads in Reported Property (as a response and communicate synchronization status) 
            _bCelcius = desiredProperties["isCelcius"]["value"];

            TwinCollection twinValue = new TwinCollection();
            twinValue["value"] = _bCelcius;
            twinValue["desiredVersion"] = desiredProperties["$version"];
            twinValue["statusCode"] = 200;
            twinValue["status"] = "completed";

            TwinCollection reportedProperties = new TwinCollection();
            reportedProperties["isCelcius"] = twinValue;

            await _client.UpdateReportedPropertiesAsync(reportedProperties).ConfigureAwait(false);
        }
    }
}
