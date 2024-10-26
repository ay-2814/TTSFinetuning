using Azure;
using Azure.AI.OpenAI;
using Azure.AI.Translation.Text;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Net.Http;
using Newtonsoft.Json.Linq;

using System.Linq;

using System;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Threading;
using System.ComponentModel;
using System;
using System.Collections.Generic;
using Azure;
using Azure.Communication.Email;
// add back to main repo as branch in future. Mb gang thought it wouldnt work with seperate resources. Was being dumb
var builder = WebApplication.CreateBuilder(args);

//string thingy=builderier.Get<string><"AcsConnectionString">>();


//Criminal Employees:Attorney - Chris,Attorney - Ben,Paralegal - Sam,Intake Specialist Liz,Family Employees:Paralegal - Jannet,Attorney - Yasmin,Intake Specialist Liz,Civil/Personal Injury Employees:Paralegal - Stephanie,Attorney - Raeanna,Intake Specialist Liz,Other Support:Kelli,Victoria
//Chris:+13177623581,Ben:+13177623559,Sam:+13177623559,Liz:+13177623598,Jannet:+13176163290,Vasmin:+13176161981,Stephanie:+13176437793,Raeanna:+13177623630,Kelli:+13177623610,Victoria:+13177623598
//For Sending to Zapier
HttpClient comunicator = new HttpClient();

//Get ACS Connection String from appsettings.json
//get from associated number in future?
var builderier =builder.Configuration.GetSection("unchanging");
var acsConnectionString = builderier["AcsConnectionString"];
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);
//Call Automation Client
var client = new CallAutomationClient(connectionString: acsConnectionString);
//Grab the Cognitive Services endpoint from appsettings.json
var cognitiveServicesEndpoint = builderier["CognitiveServiceEndpoint"];
ArgumentNullException.ThrowIfNullOrEmpty(cognitiveServicesEndpoint);

var key = builderier["AzureOpenAIServiceKey"];
ArgumentNullException.ThrowIfNullOrEmpty(key);

var endpoint = builderier["AzureOpenAIServiceEndpoint"];
ArgumentNullException.ThrowIfNullOrEmpty(endpoint);

string transendpoint = new(builderier["TextTranslationEndpoint"]);
AzureKeyCredential credential = new(builderier["TextTranslationKey"]);
TextTranslationClient transclient = new(credential, new Uri(transendpoint));
//saves on translation costs
string[][] langlist = {
    new string[] {"english","en-US","press for english"},
    new string[] {"spanish","es-ES","presione para español"},
    new string[] {"german","de-DE","Drücken Sie fuer Deutsch"}};


var ai_client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));

//Register and make CallAutomationClient accessible via dependency injection
builder.Services.AddSingleton(client);
builder.Services.AddSingleton(ai_client);
var app = builder.Build();

var devTunnelUri = builderier["DevTunnelUri"];
ArgumentNullException.ThrowIfNullOrEmpty(devTunnelUri);
var maxTimeout = 2;
/* You also need to determine the intent of the customer query and classify the conversation into one of these categories: non-law related issue, new case, returning client
    Use the below format, replacing the text in brackets with the result. Do not include the brackets in the output: 
    Content:[Answer the customer briefly and clearly in two lines] 
    Intent:[Determine the intent of the customer query] 
    Category:[Classify the intent into one of the categories]*/

//outside vars

string chatResponseExtractPattern = @"\s*Content:(.*)\s*Intent:(.*)\s*Category:(.*)";





app.MapGet("/", () => "Hello ACS CallAutomation!");
//needed for hangup outside of main place
int i = 0;
app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
         
        //think this might work, but gotta test more. Fixing phantom clone events
         if (eventGridEvent != eventGridEvents[^1] && eventGridEvent==eventGridEvents[^2]) {
             break;
         }
         // Handle system events
        // Handle the subscription validation event.
         if (eventGridEvent.TryGetSystemEventData(out object eventData))
        {
            // Handle the subscription validation event.
            if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            {
                var responseData = new SubscriptionValidationResponse
                {
                    ValidationResponse = subscriptionValidationEventData.ValidationCode
                };
                return Results.Ok(responseData);
            }
        }
         Thread t = new Thread(() => NewCall(eventGridEvent));
         t.Start();
        //reset chat
        
    }
    return Results.Ok();
});

// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger) =>
{
    var eventProcessor = client.GetEventProcessor();
    eventProcessor.ProcessEvents(cloudEvents);
    return Results.Ok();
});
async Task NewCall(EventGridEvent eventGridEvent  ) {
        //General, Always Needed
        string nim="+"+eventGridEvent.Data.ToString().Substring(eventGridEvent.Data.ToString().IndexOf("4:")+8,11);
        Console.WriteLine($"Number {nim} called");

        var builderierer = builder.Configuration.GetSection(nim);

        string forwardtonumber = builderierer["ForwardToNumber"];
        string companyname = builderierer["CompanyName"];
        string mailtoaddress = builderierer["MailToAddress"];
        string prompttype = builderierer["PromptType"];
        string addtoprompt = builderierer["AddToPrompt"];
        string passthroughphrase =builderierer["PassThroughPhrase"];
        string employeenames = builderierer["EmployeeNames"];
        string[] listemployeenames = {builderierer["EmployeeNames"]};
        string relateddoings = builderierer["RelatedDoings"];
        string employeenumbers =builderierer["EmployeeNumbers"];
        string whitelistednumbers =builderierer["WhiteListedNumbers"];
        bool specialtiming =bool.Parse(builderierer["SpecialTiming"]);
        string answertimes =builderierer["AnswerTimes"];
        string[] answerdays =builderierer["AnswerDays"].Split(";");
        string[] languages =builderierer["Languages"].Split(",");
        string calllang="en-US";
        //Set Specific Voice
        var voice = "en-US-LunaNeural";
        //relative to GMT, an int
        string timezone =builderierer["TimeZone"];
        Dictionary<string, string> promptDictionary = new Dictionary<string, string>(){
        {"Legal", $"""
        You are a lawyer's receptionist.
        You work for {companyname}.
        Your lawfirm has these employees: {employeenames}.
        Your firm practices: {relateddoings}.
        Important Guidelines:
        Very important: Make sure to wait for the caller to finish speaking and DO NOT INTERUPT THE CALLER!!
        Very important: Do not repeat yourself!! Do not keep saying Im sorry to hear that. Do not say thank you for the information twice! Saying it once is enough. Do not repeat questions if they have been anwered.
        
        Other guidelines:
        Keep responses short, and concise.
        Be positive, supportive, friendly, and empathetic to the caller in your resposes.
        Be very smart, helpful and intellectual about the law like you are a real lawyer.
        Ask only one question at a time. Do not overwhelm the caller with multiple questions.
        Do not assume the caller wants to tranfer unless they specifically ask to transfer to another lawyer.  

        Your goal is to:
        First, figure out whether the caller is calling for a legal matter. If not, act as a regular receptionist and ignore all other instructions past this point.
        Second, figure out if the area of law the case relates to is one of these: {relateddoings}.
        Third, qualify the client by inquiring very deeply about their case, but do not pester them with confirmation questions. Ask follow up questions and get as much pertinent information as possible. 
        Fourth, acquire the caller's contact information like their name, email, and phone number. Ask no more than twice if they say no initially.
        Finally, ask once if the caller wants to set up a meeting/free consultation with exactly one of your employees. If they say yes, sure, or some variation of an affirmative without a date/time, just ask what time they want to set it up. If they say no, or a variation of a negative, then they dont want to set up a meeting, so you should move on to the end of the call. 
        Record the details of the requested meeting. 
        Say that you will inform the lawyer of the requested meeting time and will confrim with you via email. Do not assert that the meeting is already fixed becasue you do not know the lawyer's avalablility.
        DO NOT mention free consultations with another lawyer. 

        Make sure that you thank them for calling and giving this very helpful information. Reassure the lawyers will do their best to help with the case and that they value the client. 
        If you have acquired all the information you need and the caller does not have any more questions and doesn't wait say "Goodbye". 
        Do not comment on the validity/strength of their case or whether it's likely the law firm will take it.
        You cannot hold the call.
        If you are going to refer someone to a lawyer, refer them to one of your fellow employees.
        {addtoprompt}
        """},
        /*{"RealEstate", $"""
        You are a realestate office's receptionist.
        You work for {companyname}.
        Your goal is to:
        First, acquire the caller's contact information.
        Second, qualify the client by inquiring about their specific needs.
        Third, keep answers non-repetitive and concise.
        If you have acquired all the information you need or feel like the conversation isn't appropriate say "Goodbye".
        Your company deals with: {relateddoings}
        You cannot forward the call. You are the only person available.
        """}*/
        }; 
            
        string answerPromptSystemTemplate = promptDictionary[prompttype];

        string helloPrompt = $"Good Morning";

        string timeoutSilencePrompt = "I'm sorry, I didn't hear anything. Could you repeat it?";
        string goodbyePrompt = $"Thank you for calling! I'll pass this over to one of our lawyers at {companyname}. Have a great day!";
        //string connectAgentPrompt = "I'm sorry, I was not able to assist you with your request. Let me transfer you to one of our receptionists who can help you further. Please hold the line and I'll connect you shortly.";
        string callTransferFailurePrompt = ".";
        string forwardtonumberEmptyPrompt = "I'm sorry, we're currently experiencing high call volumes and all of our employees are currently busy. You will be forwarded to our next available employee.";
        string EndCallPhraseToConnectAgent = "Please stay on the line. I'm going to transfer you to ";
        string transferWarningPrompt = "I'd be happy to transfer you to one of our employees, but I assure you I'm perfectly equipped to handle your call. If you'd still like to be transfered, no worries, just let me know.";

        string transferFailedContext = "TransferFailed";
        string transferWarningContext = "TransferWanted";
        string connectAgentContext = "ConnectAgent";
        string goodbyeContext = "Goodbye";
        string transcript="";

        bool transferme=false;
        bool disconnected=false;
        string[] order;
        int time=Int32.Parse(DateTime.UtcNow.AddHours(Int32.Parse(timezone)).ToString("HH:mm").Substring(0,2));
        /*if (open) {
            order=numberorderopen;
        } else {
            order=numberorderclosed;
        }*/
        //alldays of week must be defined rn
        
        if (time>12) {
            helloPrompt =  $"Good Afternoon";

        } else {
            helloPrompt = $"Good Morning";

        }
        helloPrompt+=$", thank you for calling, this is {companyname}'s AI Receptionist, Luna, how can I help you?";
        Console.WriteLine($"Incoming Call event received.");
        transcript="Receptionist: "+helloPrompt;
        
        var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
        var callerId = Helper.GetCallerId(jsonObject);
        var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
        var rejectOption = new RejectCallOptions(incomingCallContext); 
        rejectOption.CallRejectReason = CallRejectReason.Forbidden; 
        var callbackUri = new Uri(new Uri(devTunnelUri), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
        Console.WriteLine($"Callback Url: {callbackUri}");
        
        var options = new AnswerCallOptions(incomingCallContext, callbackUri)
        {
            CallIntelligenceOptions = new CallIntelligenceOptions() { CognitiveServicesEndpoint = new Uri(cognitiveServicesEndpoint) }
        };
        AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
        Console.WriteLine($"Call connecting");

        var answer_result = await answerCallResult.WaitForEventProcessorAsync();
        Console.WriteLine($"Tried Connecting");
        
        if (answer_result.IsSuccess)
        {
            Console.WriteLine($"Call connected event received for connection id: {answer_result.SuccessResult.CallConnectionId}");
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();
            if (languages.Length>1) {
                voice="en-US-AvaMultilingualNeural";
                await LanguageRecognizeAsync(callConnectionMedia, callerId, languages);
            }
            else {
            await HandleRecognizeAsync(callConnectionMedia, callerId, helloPrompt, voice, calllang);
            }
        }
        var chatCompletionsOptions = new ChatCompletionsOptions()
            {
                Messages = {
                    new ChatMessage(ChatRole.System, answerPromptSystemTemplate),
                    new ChatMessage(ChatRole.Assistant, helloPrompt),
                    },
                    
                MaxTokens = 1000,
                FrequencyPenalty = 1.5f,
                PresencePenalty = 1.2f
            };
        

        

        
        
        
        //Use EventProcessor to process CallConnected event
        
        Console.WriteLine($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
        string[] posdates = {"M","T","W","Th","F","Sa","Su"};
        string weekday= posdates[(int)DateTime.UtcNow.AddHours(Int32.Parse(timezone)).DayOfWeek-1];
        if (specialtiming) {
            string timin="";
            foreach (string weeksections in answerdays) {
                if (weeksections.Contains(weekday)) {

                    timin=builderierer["AnswerTimes"].Split(";")[Array.IndexOf(answerdays,weeksections)];
                }
            }

            // could put thing here if it isn't defined
            //easily just eval with ! infront i was just lazy
            if (timin.Substring(timin.IndexOf(":")+1)!="c") {
            if (timin.Contains("!")) {
                if (time<Int32.Parse(timin.Substring(1,2)) || time>Int32.Parse(timin.Substring(2, 2))) {
                    ForwardToDefault(timin.Substring(timin.IndexOf(":")+1), answerCallResult);
                }
            }
            else {
                if (time>=Int32.Parse(timin.Substring(0,2)) && time<=Int32.Parse(timin.Substring(2, 2))) {
                    ForwardToDefault(timin.Substring(timin.IndexOf(":")+1), answerCallResult);
                }
            }
            }

        }
        else if (!(time>=Int32.Parse(answertimes.Substring(0,2)) && time<=Int32.Parse(answertimes.Substring(2)))) {
            ForwardToDefault(forwardtonumber, answerCallResult);
        }
       

        if (whitelistednumbers.Contains(callerId.Substring(2))) {
            ForwardToDefault(forwardtonumber, answerCallResult);
        }
        /*StartMediaStreamingOptions options = new StartMediaStreamingOptions() 
            { 
                OperationCallbackUri = callbackUri, 
                OperationContext = "startMediaStreamingContext" 
            };
        await callMedia.StartMediaStreamingAsync(options); 
        var recordoptions =
        new StartRecordingOptions(new ServerCallLocator(answerCallResult.CallConnectionProperties.ServerCallId))
        {
            RecordingContent = RecordingContent.Audio,
            RecordingChannel = RecordingChannel.Unmixed,
            RecordingFormat = RecordingFormat.Wav,
            RecordingStorageKind = RecordingStorageKind.AzureCommunicationServices,
            RecordingStateCallbackUri = callbackUri
        };
        var recresponse = await client.GetCallRecording().StartAsync(recordoptions).ConfigureAwait(false);*/
        client.GetEventProcessor().AttachOngoingEventProcessor<CallDisconnected>(answerCallResult.CallConnection.CallConnectionId, async (calldisconnectedEvent) =>
        {
            disconnected=true;
            Console.WriteLine("Transcript Length: " + transcript.Length);
            if (transcript.Length>=150) {
            
            chatCompletionsOptions = new ChatCompletionsOptions()
                {
                    Messages = {
                        new ChatMessage(ChatRole.System, $"""
                The text passed to you is a transcription of a call between a receptionist and a caller.
                You work for {companyname}, the same company that the receptionist works for.
                Your job is to extract important details from the transcript that a lawyer could use for a case regarding the client or if there is no case simply summarize it.
                Do not try and continue the transcript.
                Do not include information describing simple actions performed by parties in the call or the parties themselves.
                Always summarize the transcript in bullet points.
            """),
                        },
                    MaxTokens = 1000
                };
            var summary = await GetChatGPTResponse(transcript, chatCompletionsOptions, builderier["FancierAzureOpenAIDeploymentModelName"]);
            Console.WriteLine("Summary: "+ summary);
            //intention?
            chatCompletionsOptions = new ChatCompletionsOptions()
                {
                    Messages = {
                        new ChatMessage(ChatRole.System, """
                You need to determine the name, number, and email of the caller is the supplied transcript.
                Use the below format, replacing the text in brackets with the result. Do not include the brackets in the output: 
                Name:[The caller's first and last name] 
                Number:[The caller's Number] 
                Email:[The caller's Email]
            """),
                        },
                    MaxTokens = 1000
                };
            string chatResponseExtractPattern2 = @"\s*Name:(.*)\s*Number:(.*)\s*Email:(.*)";
            var info = await GetChatGPTResponse(transcript, chatCompletionsOptions);
            Console.WriteLine(info);
            chatCompletionsOptions = new ChatCompletionsOptions()
                {
                    Messages = {
                        new ChatMessage(ChatRole.System, $"""
                The text passed to you is a transcription of a call between a receptionist and a caller.
                Determine whether it's likely the call is a spam, fishing, or generally unwanted call.
                Use the below format, replacing the text in brackets with the result.
                Spam-Likely: [Whether the caller is suspicious or nefarious]
            """),
                        },
                    MaxTokens = 1000
                };
            var spam = await GetChatGPTResponse(transcript, chatCompletionsOptions);
            string chatResponseExtractPattern3 = @"\s*Spam-Likely:(.*)";
            Regex respam = new Regex(chatResponseExtractPattern3);
            Match mspam = respam.Match(spam);
            Regex regex = new Regex(chatResponseExtractPattern2);
            Match match = regex.Match(info);
            string name = "Unknown Name";
            var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
            string number = Helper.GetCallerId(jsonObject).Substring(2);
            string email = "";
            string body="Contact at "+number+"\n\nCase Summary:\n"+summary;

            if (match.Success)
            {
                name = match.Groups[1].Value;
                if (match.Groups[2].Value.Any(char.IsDigit)) {
                    number=match.Groups[2].Value;
                }
                body="Contact at "+number+"\n\nCase Summary:\n"+summary;
                if (match.Groups[3].Value.Contains('@')) {
                    email = match.Groups[3].Value;
                    body="Contact at "+number+" or at "+email+"\n\nCase Summary:\n"+summary;


                }
            }
            //var requestContent = new FormUrlEncodedContent(values);
            //await comunicator.PostAsync(
            //    "https://hooks.zapier.com/hooks/catch/19433423/237wz9n/",
            //requestContent);
            
            string subject = "Possible Client from Caseflood.ai: "+name;
            if (mspam.Success) {
                try {
                    if (bool.Parse(mspam.Groups[0].Value)) {
                        subject.Insert(0,"Warning: Likely Nefarious Caller, ");
                    }
                }
                catch {

                }
            }
            var toRecipients = new List<EmailAddress>
                {
                //new EmailAddress("caseflooddev@gmail.com"),

                //new EmailAddress("tolenschreid@gmail.com")
                //new EmailAddress("chris@eskewlaw.com"),
                //new EmailAddress("victoria@eskewlaw.com"),
                //new EmailAddress("kelli@eskewlaw.com")
                };
            //add in sending to mulitple
            if (mailtoaddress!="") {
                var mlist=mailtoaddress.Split(",");
                //can prolly be moved up honestly. could save time
                foreach (string m in mlist) {
                    toRecipients.Add(new EmailAddress(m));
                }
            }
            EmailRecipients emailRecipients = new EmailRecipients(toRecipients);
            var emailContent = new EmailContent(subject)
            {
                PlainText = body,
                Html = ""
            };

// This code retrieves your connection string from an environment variable.
            var emailClient = new EmailClient(builderier["EmailConnectionString"]);
            
                EmailSendOperation emailSendOperation = emailClient.Send(
                WaitUntil.Completed,
                new EmailMessage(
                senderAddress: "DoNotReply@caseflood.ai",
                emailRecipients,
                emailContent)
                );
                    }});
        client.GetEventProcessor().AttachOngoingEventProcessor<PlayFailed>(answerCallResult.CallConnection.CallConnectionId, async (playFailedEvent) =>
        {
            Console.WriteLine($"Play failed event received for connection id: {playFailedEvent.CallConnectionId}. Hanging up call...");
            await answerCallResult.CallConnection.HangUpAsync(true);
        });
        /*client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferAccepted>(answerCallResult.CallConnection.CallConnectionId, async (callTransferAcceptedEvent) =>
        {
            Console.WriteLine($"Call transfer accepted event received for connection id: {callTransferAcceptedEvent.CallConnectionId}.");
        });*/https://dev-g7g2bbape4haf2ap.eastus-01.azurewebsites.net
        client.GetEventProcessor().AttachOngoingEventProcessor<CallTransferFailed>(answerCallResult.CallConnection.CallConnectionId, async (callTransferFailedEvent) =>
        {
            Console.WriteLine($"Call transfer failed event received for connection id: {callTransferFailedEvent.CallConnectionId}.");
            var resultInformation = callTransferFailedEvent.ResultInformation;
            //Console.WriteLine("Encountered error during call transfer, message={msg}, code={code}, subCode={subCode}", resultInformation?.Message, resultInformation?.Code, resultInformation?.SubCode);
            /*if (calledorderindex!=order.Length) {
                if (order[calledorderindex]!="") {
                Console.WriteLine($"Initializing the Call transfer...");
                CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(order[calledorderindex]);
                TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                Console.WriteLine($"Transfer call initiated: {result.OperationContext}");
                calledorderindex++;
                //transferme=false;
                } else {

                }
            }*/
            


        });
        

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeCompleted>(answerCallResult.CallConnection.CallConnectionId, async (recognizeCompletedEvent) =>
        {
            //Todo: play all hmm
            var dtmf = recognizeCompletedEvent.RecognizeResult as DtmfResult;
            if (!string.IsNullOrWhiteSpace(dtmf?.Tones[0].ToString())) {
                try {
                    string[] dtones={"one","two","three","four","five","six","seven","eight","nine","zero"};
                    string langinenglish = languages[Array.IndexOf(dtones,dtmf.Tones[0].ToString())];
                    foreach (string[] lang in langlist) {
                        if (lang[0]==langinenglish) {
                            calllang=lang[1];
                        }
                    }
                    Console.WriteLine(calllang);
                    chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.System, "You may only respond in "+langinenglish+" from now on."));    
                    await HandleRecognizeAsync(answerCallResult.CallConnection.GetCallMedia(), callerId, helloPrompt, voice, calllang);
                }
                catch {
                    await LanguageRecognizeAsync(answerCallResult.CallConnection.GetCallMedia(), callerId, languages);
                }
            } else {

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"Recognize completed event received for connection id: {recognizeCompletedEvent.CallConnectionId}");
            var speech_result = recognizeCompletedEvent.RecognizeResult as SpeechResult;
           
           /* client.GetCallRecording().StopAsync(recresponse.Value.RecordingId);
            await DownloadToAsync(stor,)
            var playOptions = new PlayToAllOptions(recordoptions.RecordingContent.Audio) { OperationContext = "Hello" };
            await answerCallResult.CallConnection.GetCallMedia().PlayToAllAsync(playOptions);*/
            
            //var audio_result = recognizeCompletedEvent.RecordingContent as Audio;

            
            if (!string.IsNullOrWhiteSpace(speech_result?.Speech))
            {
                Console.WriteLine($"Recognized speech: {speech_result.Speech}");
                //employeenames must be defined well or it will auto forward.
                if (passthroughphrase!="" && speech_result.Speech.ToLowerInvariant().Contains(passthroughphrase.ToLowerInvariant())) {
                        Console.WriteLine(forwardtonumber);
                        CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(forwardtonumber);
                        await HandlePlayAsync("Yes of course, patching you through to him right away.",
                               connectAgentContext, answerCallResult.CallConnection.GetCallMedia(), voice, calllang);
                        TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                        Console.WriteLine($"Transfer call initiated: {result.OperationContext}");
                        //test out?
                }
                else if (transcript.ToLowerInvariant().Contains("forward") ||  speech_result.Speech.ToLowerInvariant().Contains("schedule") || speech_result.Speech.ToLowerInvariant().Contains("transfer") || listemployeenames.Any(s => speech_result.Speech.ToLowerInvariant().Contains(s.ToLowerInvariant())) || speech_result.Speech.ToLowerInvariant().Contains("agent") || speech_result.Speech.ToLowerInvariant().Contains("question for") || speech_result.Speech.ToLowerInvariant().Contains("forwarded") ||speech_result.Speech.ToLowerInvariant().Contains("forward me") || (speech_result.Speech.ToLowerInvariant().Contains("talk to") && !speech_result.Speech.ToLowerInvariant().Contains("you")) || (speech_result.Speech.ToLowerInvariant().Contains("speak to") && !speech_result.Speech.ToLowerInvariant().Contains("you")))
                {

                    if (!transferme) {
                        transferme=true;
                        var spoken = new TextSource(transferWarningPrompt)
                            {
                                VoiceName = voice
                            };
                        StartRecogAsync(callerId,transferWarningContext, spoken, answerCallResult, calllang);
                    }
                    else {
                    chatCompletionsOptions = new ChatCompletionsOptions()
                        {
                            Messages = {
                                new ChatMessage(ChatRole.System, $"""
                            You need to determine the area of law the case relates to from:{relateddoings} and the person you should forward the call to based off of that area and this:{employeenames}.
                            Use the below format, replacing the text in brackets with the result. Do not include the brackets in the output: 
                            Area Of Law:[Area of Law the case Pertains to] 
                            Employee:[Employee the call should be forwarded to]
                        """),
                                },
                            MaxTokens = 1000
                        };
                    string chatResponseExtractPattern2 = @"\s*Area Of Law:(.*)\s*Employee:(.*)";
                    var info = await GetChatGPTResponse(transcript, chatCompletionsOptions);
                    Regex regex = new Regex(chatResponseExtractPattern2);
                    Match match = regex.Match(info);
                    //might add back up if it messes up
                    Console.WriteLine($"Chat GPT response: {info}");
                    transcript+="Receptionist: "+"I'm going to be forwarding you to "+match.Groups[2].Value+" who is an expert in the areas relating to your case.";
                    await HandlePlayAsync("I'm going to be forwarding you to "+match.Groups[2].Value+" who is an expert in the areas relating to your case.",
                               connectAgentContext, answerCallResult.CallConnection.GetCallMedia(), voice, calllang);
                    Console.WriteLine($"Initializing the Call transfer...");
                    Console.WriteLine(match.Groups[2].Value+"");
                    try {
                        string fnum=employeenumbers.Substring(employeenumbers.IndexOf(match.Groups[2].Value)+match.Groups[2].Value.Length+1,12);
                        //didnt wanna count hehe
                        if (employeenumbers.Contains(fnum) && fnum.Length=="+18505598228".Length) {
                            Console.WriteLine(fnum);
                            CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(fnum);
                            TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                            Console.WriteLine($"Transfer call initiated: {result.OperationContext}");
                        } else {
                            Console.WriteLine(forwardtonumber);
                            CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(forwardtonumber);
                            TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                            Console.WriteLine($"Transfer call initiated: {result.OperationContext}");
                        }
                    }
                    catch {
                        Console.WriteLine(forwardtonumber);
                        CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(forwardtonumber);
                        TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
                        Console.WriteLine($"Transfer call initiated: {result.OperationContext}");
                    }
                    
                    transferme=false;
                    //test out?
                   }
                } else if (transcript.ToLowerInvariant().Contains("goodbye") && !speech_result.Speech.ToLowerInvariant().Contains("don't") && !speech_result.Speech.ToLowerInvariant().Contains("wait")) {
                    Console.WriteLine($"Disconnecting the call...");
                    if (!disconnected) {
                        await answerCallResult.CallConnection.HangUpAsync(true);

                    }
                }
                else
                {                    
                    transcript+="Caller: "+speech_result.Speech;
                    var chatGPTResponse = await GetChatGPTResponse(speech_result.Speech,  chatCompletionsOptions);
                    Console.WriteLine($"Chat GPT response: {chatGPTResponse}");
                    transcript+="Receptionist: "+chatGPTResponse;
                   
                        if (!disconnected) {
                        await HandleChatResponse(chatGPTResponse, answerCallResult.CallConnection.GetCallMedia(), callerId, voice, calllang);
                         watch.Stop();
                    Console.WriteLine($"Response Time: {watch.ElapsedMilliseconds}");
                        }
                        
                        //}
                        
                    //}
                }
            // Runs Everytime something is said

            //watch.Stop();
            //Console.WriteLine($"Response Time: {watch.ElapsedMilliseconds}");
            
            } 
            }
        });

        client.GetEventProcessor().AttachOngoingEventProcessor<RecognizeFailed>(answerCallResult.CallConnection.CallConnectionId, async (recognizeFailedEvent) =>
        {
            var callConnectionMedia = answerCallResult.CallConnection.GetCallMedia();

            if (MediaEventReasonCode.RecognizeInitialSilenceTimedOut.Equals(recognizeFailedEvent.ResultInformation.SubCode.Value.ToString()) && maxTimeout > 0)
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Retrying recognize...");
                maxTimeout--;
                //await HandleRecognizeAsync(callConnectionMedia, callerId, timeoutSilencePrompt, voice, calllang);
            }
            else
            {
                Console.WriteLine($"Recognize failed event received for connection id: {recognizeFailedEvent.CallConnectionId}. Playing goodbye message...");
                transcript+="Receptionist: "+goodbyePrompt;
                //await HandlePlayAsync(goodbyePrompt, goodbyeContext, callConnectionMedia, voice);
            }
        });
}

async Task HandleChatResponse(string chatResponse, CallMedia callConnectionMedia, string callerId,  string voice, string calllang, string context = "OpenAISample")
{
    //Console.WriteLine("Response Length: "+chatResponse.Length);
    //gonna have to change language later
    string ssml=$"""
    <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-US">
        <voice name="{voice}">
            <mstts:express-as style="assistant" styledegree="2">
                <lang xml:lang="{calllang}">
                    {chatResponse}
                </lang>{"\n\t    "}
            </mstts:express-as>
        </voice>
    </speak>
    """; 
    var chatGPTResponseSource = new SsmlSource(ssml);
    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(30),
            Prompt = chatGPTResponseSource,
            OperationContext = context,
            SpeechLanguage = calllang,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };
    //var playResponse = await callConnectionMedia.PlayToAllAsync(recognizeOptions);
    var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
    //await StartRecogAsync(callerId, context, chatGPTResponseSource);
    //recognize_result.WaitForEventProcessorAsync();

}

async Task StartRecogAsync(string callerId, string context, dynamic prompt, AnswerCallResult answerCallResult, string calllang) {
    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(30),
            Prompt = prompt,
            OperationContext = context,
            SpeechLanguage = calllang,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(500)
        };
    //var playResponse = await callConnectionMedia.PlayToAllAsync(recognizeOptions);
    var recognize_result = await answerCallResult.CallConnection.GetCallMedia().StartRecognizingAsync(recognizeOptions);
}
/*int getSentimentScore(string sentimentScore)
{
    string pattern = @"(\d)+";
    Regex regex = new Regex(pattern);
    Match match = regex.Match(sentimentScore);
    return match.Success ? int.Parse(match.Value) : -1;
}*/

//messes up regular stuff plus not specficially needed

/*async Task<bool> DetectEscalateToAgentIntent(string speechText) =>
           await HasIntentAsync(userQuery: speechText, intentDescription: "talk to agent");

async Task<bool> HasIntentAsync(string userQuery, string intentDescription)
{
    var systemPrompt = "You are a helpful assistant";
    var baseUserPrompt = "In 1 word: does {0} have similar meaning as {1}?";
    var combinedPrompt = string.Format(baseUserPrompt, userQuery, intentDescription);

    var response = await GetOneShotChatCompletionsAsync(systemPrompt, combinedPrompt);

    var isMatch = response.ToLowerInvariant().Contains("yes");
    //Console.WriteLine($"OpenAI results: isMatch={isMatch}, customerQuery='{userQuery}', intentDescription='{intentDescription}'");
    return isMatch;
}*/

async Task<string> GetChatGPTResponse(string speech_input,  ChatCompletionsOptions chatCompletionsOptions, string deploymentname="lil-MissKind-Luna")
{
    //A value that influences the probability of generated tokens appearing based on their cumulative frequency in generated text. 
    //Positive values will make tokens less likely to appear as their frequency increases and decrease the likelihood of the model repeating the same statements verbatim. Supported range is [-2, 2].
    return await GetChatCompletionsAsync(speech_input, chatCompletionsOptions, deploymentname);
}

/*async Task<string> GetOneShotChatCompletionsAsync(string systemPrompt, string userPrompt)
{
     var oneshotchatCompletionsOptions = new ChatCompletionsOptions()
    {
        Messages = {
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                    },
        MaxTokens = 1000
    };
    var response = await ai_client.GetChatCompletionsAsync(
        deploymentOrModelName: builderier["AzureOpenAIDeploymentModelName"],
        chatCompletionsOptions);
    var response_content = response.Value.Choices[0].Message.Content;
    return response_content;
}*/

async Task<string> GetChatCompletionsAsync( string userPrompt, ChatCompletionsOptions chatCompletionsOptions, string deploymentname)
{
    chatCompletionsOptions.Messages.Add(new ChatMessage(ChatRole.User, userPrompt));
    var response = await ai_client.GetChatCompletionsAsync(
        deploymentOrModelName: deploymentname,
        chatCompletionsOptions);
    var response_content = response.Value.Choices[0].Message.Content;
    return response_content;
}

async Task HandleRecognizeAsync(CallMedia callConnectionMedia, string callerId, string message, string voice, string calllang)
{
    // Play greeting message
    Response<IReadOnlyList<TranslatedTextItem>> response = await transclient.TranslateAsync(calllang, message).ConfigureAwait(false);
    IReadOnlyList<TranslatedTextItem> translations = response.Value;
    TranslatedTextItem translation = translations.FirstOrDefault();
    
    string ssml=$"""
    <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-US">
        <voice name="{voice}">
            <mstts:express-as style="assistant" styledegree="2">
                <lang xml:lang="{calllang}">
                    {translation?.Translations?.FirstOrDefault()?.Text}
                </lang>
            </mstts:express-as>
        </voice>
    </speak>
    """; 
    var greetingPlaySource = new SsmlSource(ssml);

    var recognizeOptions =
        new CallMediaRecognizeSpeechOptions(
            targetParticipant: CommunicationIdentifier.FromRawId(callerId))
        {
            InterruptPrompt = false,
            InitialSilenceTimeout = TimeSpan.FromSeconds(30),
            Prompt = greetingPlaySource,
            OperationContext = "GetFreeFormText",
            SpeechLanguage = calllang,
            EndSilenceTimeout = TimeSpan.FromMilliseconds(1000)
        };

     var recognize_result = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

async Task LanguageRecognizeAsync(CallMedia callConnectionMedia, string callerId, string[] languages)
{
    // Play greeting message
    string ssml="";
    int x=0;
   string inside="";
    string[] langli = {"english","en-US","press for english"};

    foreach (string language in languages) {  
        langli=Array.Find(langlist, element => element[0] == language);
        x++;
        //jank tabbing mb gang.
        if (x==languages.Length) {
            inside+=$"""
            <lang xml:lang="{langli[1]}">
                            {langli[2].Split(" ")[0]+" "+x+" "+langli[2].Substring(langli[2].IndexOf(" ")+1)}
                        </lang>
            """; 
        } else {
            inside+=$"""
            <lang xml:lang="{langli[1]}">
                            {langli[2].Split(" ")[0]+" "+x+" "+langli[2].Substring(langli[2].IndexOf(" ")+1)}
                        </lang>{"\n\t    "}
            """; 
        }
       
        
        
    }
    ssml=$"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-US">
                <voice name="en-US-AmandaMultilingualNeural">
                    <mstts:express-as style="assistant" styledegree="2">
                        {inside}
                    </mstts:express-as>
                </voice>
            </speak>
            """;
    var greetingPlaySource = new SsmlSource(ssml);

    var recognizeOptions = new CallMediaRecognizeDtmfOptions( CommunicationIdentifier.FromRawId(callerId), 1) {
        InitialSilenceTimeout = TimeSpan.FromSeconds(30),
        Prompt = greetingPlaySource,
        InterToneTimeout = TimeSpan.FromSeconds(5),
        InterruptPrompt = true,
        StopTones = new DtmfTone[] {
        DtmfTone.Pound
    },
};
var recognizeResult = await callConnectionMedia.StartRecognizingAsync(recognizeOptions);
}

async Task HandlePlayAsync(string textToPlay, string context, CallMedia callConnectionMedia, string voice, string calllang)
{
    // Play message
     Response<IReadOnlyList<TranslatedTextItem>> response = await transclient.TranslateAsync(calllang, textToPlay).ConfigureAwait(false);
    IReadOnlyList<TranslatedTextItem> translations = response.Value;
    TranslatedTextItem translation = translations.FirstOrDefault();              


    
    string ssml=$"""
    <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-US">
        <voice name="{voice}">
            <mstts:express-as style="assistant" styledegree="2">
                <lang xml:lang="{calllang}">
                    {translation?.Translations?.FirstOrDefault()?.Text}
                </lang>
            </mstts:express-as>
        </voice>
    </speak>
    """; 
    var playSource = new SsmlSource(ssml);

    var playOptions = new PlayToAllOptions(playSource) { OperationContext = context };
    await callConnectionMedia.PlayToAllAsync(playOptions);
    //Console.WriteLine("hello");
}
async Task ForwardToDefault(string number, AnswerCallResult answerCallResult)
{
    Console.WriteLine("forward");

    CommunicationIdentifier transferDestination = new PhoneNumberIdentifier(number);
    TransferCallToParticipantResult result = await answerCallResult.CallConnection.TransferCallToParticipantAsync(transferDestination);
}

app.Run();